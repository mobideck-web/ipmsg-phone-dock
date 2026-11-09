using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace IPMsg.Dock.Setup
{
    internal static class InstallerChecks
    {
        private static int passed;
        private static string workspace;
        private static int serial;

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1) throw new ArgumentException("Provide a new fixture directory.");
                workspace = SafePaths.Root(args[0]);
                if (Directory.Exists(workspace) || File.Exists(workspace)) throw new IOException("Fixture directory already exists.");
                Directory.CreateDirectory(workspace);
                CheckPaths();
                CheckArchive();
                CheckInstallAndRollback();
                CheckReparse();
                CheckIntegrationFixtures();
                Console.WriteLine("PASS: " + passed + " installer safety checks. Fixtures: " + workspace);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void CheckPaths()
        {
            Assert(SafePaths.Relative("licenses/source.tar.gz") == "licenses/source.tar.gz", "ordinary payload paths");
            foreach (string unsafeName in new[] { "../escape", "/escape", "C:/escape", "a\\escape", "a//b", "a/../b", "a/./b", "a./b", "a /b", "NUL.txt", "com1.exe", "lpt\u00b2.txt", "a:b", "a?b", ".ipmsgdock-install.tsv" })
                Reject(delegate { SafePaths.Relative(unsafeName); }, "reject path " + unsafeName);
            Reject(delegate { SafePaths.Root(Path.GetPathRoot(workspace)); }, "reject drive root");
        }

        private static void CheckArchive()
        {
            Dictionary<string, string> files = Payload("first");
            Receipt package = Describe(files);
            string stage = Next("verify");
            using (Stream zip = Zip(files, null, false)) InstallerEngine.VerifyArchive(zip, package, stage);
            Assert(File.ReadAllText(Path.Combine(stage, "IPMsgDock.exe")) == "first", "verified extraction");
            Reject(delegate { using (Stream zip = Zip(files, null, false)) InstallerEngine.VerifyArchive(zip, package, stage); }, "reject occupied staging directory");
            Dictionary<string, string> tampered = new Dictionary<string, string>(files);
            tampered["IPMsgDock.exe"] = "other"; // same byte length; this must fail the SHA-256 check.
            Reject(delegate { using (Stream zip = Zip(tampered, null, false)) InstallerEngine.VerifyArchive(zip, package, null); }, "reject changed SHA-256");
            Dictionary<string, string> omitted = new Dictionary<string, string>(files);
            omitted.Remove("licenses/LICENSE.txt");
            Reject(delegate { using (Stream zip = Zip(omitted, null, false)) InstallerEngine.VerifyArchive(zip, package, null); }, "reject missing file");
            Reject(delegate { using (Stream zip = Zip(files, "../escape", false)) InstallerEngine.VerifyArchive(zip, package, null); }, "reject ZIP traversal");
            Reject(delegate { using (Stream zip = Zip(files, "IPMSGDOCK.EXE", false)) InstallerEngine.VerifyArchive(zip, package, null); }, "reject case duplicate");
            Reject(delegate { using (Stream zip = Zip(files, "unlisted.txt", false)) InstallerEngine.VerifyArchive(zip, package, null); }, "reject unlisted file");
            Reject(delegate { using (Stream zip = Zip(files, null, true)) InstallerEngine.VerifyArchive(zip, package, null); }, "reject ZIP symbolic link");
            using (MemoryStream manifest = new MemoryStream(Encoding.UTF8.GetBytes("IPMSGDOCK-PAYLOAD-1\nF\t" + new string('a', 64) + "\t1\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes("../escape")) + "\n")))
                Reject(delegate { Receipt.Read(manifest, true); }, "reject receipt traversal");
        }

        private static void CheckInstallAndRollback()
        {
            string root = Next("Programs/IPMsgDock");
            string state = Next("LocalAppData/IPMsgDock/owner.key");
            Directory.CreateDirectory(Path.GetDirectoryName(state));
            File.WriteAllText(state, "private state retained");
            Dictionary<string, string> initial = Payload("first");
            Install(root, initial, null);
            Assert(File.ReadAllText(Path.Combine(root, "IPMsgDock.exe")) == "first", "fresh install");
            Assert(File.Exists(Path.Combine(root, Receipt.FileName)), "owned-file receipt written");
            string unrelated = Path.Combine(root, "personal.txt");
            File.WriteAllText(unrelated, "unrelated");
            Install(root, Payload("second"), null);
            Assert(File.ReadAllText(Path.Combine(root, "IPMsgDock.exe")) == "second", "upgrade replaces verified owned files");
            Assert(File.ReadAllText(unrelated) == "unrelated", "upgrade preserves untracked files");
            Reject(delegate { Install(root, Payload("third"), delegate(Receipt before, Receipt after) { throw new IOException("injected integration failure"); }); }, "integration failure triggers rollback");
            Assert(File.ReadAllText(Path.Combine(root, "IPMsgDock.exe")) == "second", "rollback restores original application");
            using (FileStream receipt = File.OpenRead(Path.Combine(root, Receipt.FileName)))
                Assert(Receipt.Read(receipt, false).Files.Count == initial.Count, "rollback restores receipt");
            File.WriteAllText(Path.Combine(root, "IPMsgDock.exe"), "user modified");
            Reject(delegate { Install(root, Payload("third"), null); }, "upgrade rejects modified owned file");
            Assert(File.ReadAllText(Path.Combine(root, "IPMsgDock.exe")) == "user modified", "modified owned file preserved");
            File.WriteAllText(Path.Combine(root, "IPMsgDock.exe"), "second");
            Dictionary<string, string> collision = Payload("third");
            collision.Add("personal.txt", "overwrite forbidden");
            Reject(delegate { Install(root, collision, null); }, "upgrade rejects unowned collision");
            Assert(File.ReadAllText(unrelated) == "unrelated", "collision preserves unrelated content");
            using (FileStream locked = new FileStream(Path.Combine(root, "IPMsgDock.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
                Reject(delegate { Install(root, Payload("third"), null); }, "locked application fails before overwrite");
            Assert(File.ReadAllText(Path.Combine(root, "IPMsgDock.exe")) == "second", "locked upgrade leaves install intact");
            string unknown = Next("unknown");
            Directory.CreateDirectory(unknown);
            File.WriteAllText(Path.Combine(unknown, "existing.txt"), "preserve");
            Reject(delegate { Install(unknown, initial, null); }, "reject unrecognized installation folder");
            File.WriteAllText(Path.Combine(root, "licenses", "LICENSE.txt"), "modified license retained");
            Receipt installed;
            using (FileStream file = File.OpenRead(Path.Combine(root, Receipt.FileName))) installed = Receipt.Read(file, false);
            List<string> preserved = InstallerEngine.RemoveInstalledFiles(root, installed);
            Assert(!File.Exists(Path.Combine(root, "IPMsgDock.exe")), "uninstall removes unchanged tracked app");
            Assert(preserved.SequenceEqual(new[] { "licenses/LICENSE.txt" }), "uninstall preserves changed tracked file");
            File.Delete(Path.Combine(root, Receipt.FileName));
            InstallerEngine.RemoveEmptyDirectories(root, installed.Files.Select(delegate(OwnedFile f) { return f.Path; }));
            Assert(File.ReadAllText(unrelated) == "unrelated", "uninstall preserves unrelated file");
            Assert(File.ReadAllText(state) == "private state retained", "install and uninstall preserve separate private state");
            string cleanup = Next("cleanup");
            Directory.CreateDirectory(cleanup);
            File.WriteAllText(Path.Combine(cleanup, "owned.txt"), "owned");
            File.WriteAllText(Path.Combine(cleanup, "untracked.txt"), "preserve");
            InstallerEngine.CleanupOwnedTree(cleanup, new[] { "owned.txt" });
            Assert(File.ReadAllText(Path.Combine(cleanup, "untracked.txt")) == "preserve", "cleanup only deletes tracked file names");
        }

        private static void CheckReparse()
        {
            string destination = Next("link-destination");
            string link = Next("link");
            Directory.CreateDirectory(destination);
            bool symbolic = CreateSymbolicLink(link, destination, 0x1 | 0x2);
            bool junction = !symbolic && CreateJunction(link, destination);
            if (symbolic || junction)
            {
                try { Reject(delegate { SafePaths.Under(link, "outside.txt"); }, symbolic ? "reject directory symbolic link" : "reject directory junction"); }
                finally { Directory.Delete(link, false); }
            }
            else Console.WriteLine("SKIP: symbolic-link fixture unavailable (Windows error " + Marshal.GetLastWin32Error() + "). Reparse checks remain enabled.");
        }

        private static void CheckIntegrationFixtures()
        {
            string fixture = Next("shortcut-fixtures");
            Directory.CreateDirectory(fixture);
            string shortcutPath = Path.Combine(fixture, "fixture.lnk");
            string executable = Path.Combine(fixture, "IPMsgDock.exe");
            WindowsIntegration.CreateShortcut(shortcutPath, executable, fixture);
            Assert(File.Exists(shortcutPath), "COM creates shortcut only in fixture directory");
            object shell = null;
            object shortcut = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell", true);
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                object target = shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, new object[0]);
                object hotkey = shortcut.GetType().InvokeMember("Hotkey", BindingFlags.GetProperty, null, shortcut, new object[0]);
                Assert(string.Equals(Convert.ToString(target), executable, StringComparison.OrdinalIgnoreCase), "shortcut target matches fixture executable");
                Assert(string.IsNullOrEmpty(Convert.ToString(hotkey)), "shortcut has no competing global hotkey");
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
            byte[] contents = File.ReadAllBytes(shortcutPath);
            string other = Path.Combine(fixture, "atomic.lnk");
            WindowsIntegration.WriteShortcut(other, contents);
            Assert(File.ReadAllBytes(other).SequenceEqual(contents), "atomic shortcut creation");
            File.WriteAllText(other, "old fixture");
            WindowsIntegration.WriteShortcut(other, contents);
            Assert(File.ReadAllBytes(other).SequenceEqual(contents), "atomic shortcut replacement");
            Receipt invalid = new Receipt();
            invalid.Shortcuts.Add(FileProof.Describe(shortcutPath, shortcutPath));
            Reject(delegate { WindowsIntegration.Remove(invalid, fixture); }, "uninstall rejects arbitrary shortcut path before registry access");
            Assert(File.Exists(shortcutPath), "unrelated fixture shortcut preserved");
            Assert(!new InstallOptions().StartAtSignIn, "sign-in startup defaults off");
            Assert(WindowsIntegration.StartupCommand(fixture) == "\"" + executable + "\" --background", "startup command quotes executable and requests background mode");
            using (SetupOperation operation = new SetupOperation())
            {
                bool blocked = false;
                Thread competing = new Thread(delegate()
                {
                    try { using (SetupOperation otherOperation = new SetupOperation()) { } }
                    catch (IOException) { blocked = true; }
                });
                competing.Start();
                Assert(competing.Join(5000) && blocked, "concurrent setup operation rejected");
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(string link, string target, int flags);

        private static bool CreateJunction(string link, string destination)
        {
            Directory.CreateDirectory(link);
            using (SafeFileHandle handle = CreateFile(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) return false;
                byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + destination);
                byte[] print = Encoding.Unicode.GetBytes(destination);
                byte[] buffer = new byte[16 + substitute.Length + print.Length + 4];
                using (BinaryWriter writer = new BinaryWriter(new MemoryStream(buffer)))
                {
                    writer.Write(unchecked((int)0xa0000003));
                    writer.Write((ushort)(buffer.Length - 8));
                    writer.Write((ushort)0);
                    writer.Write((ushort)0);
                    writer.Write((ushort)substitute.Length);
                    writer.Write((ushort)(substitute.Length + 2));
                    writer.Write((ushort)print.Length);
                    writer.Write(substitute);
                    writer.Write((ushort)0);
                    writer.Write(print);
                    writer.Write((ushort)0);
                }
                int returned;
                return DeviceIoControl(handle, 0x000900a4, buffer, buffer.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero);
            }
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, int access, int share, IntPtr security, int creation, int attributes, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, int code, byte[] input, int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);

        private static void Install(string root, Dictionary<string, string> files, Action<Receipt, Receipt> integration)
        {
            string stage = Next("stage");
            Receipt package = Describe(files);
            using (Stream zip = Zip(files, null, false)) InstallerEngine.VerifyArchive(zip, package, stage);
            InstallerEngine.InstallFiles(stage, root, package, integration, null);
        }

        private static string Next(string suffix) { return Path.Combine(workspace, (++serial).ToString("D3") + "-" + suffix.Replace('/', Path.DirectorySeparatorChar)); }
        private static Dictionary<string, string> Payload(string executable)
        {
            return new Dictionary<string, string> { { "IPMsgDock.exe", executable }, { "runtime/scrcpy/scrcpy.exe", "runtime" }, { "licenses/LICENSE.txt", "license" }, { "Uninstall.exe", "uninstaller" } };
        }
        private static Receipt Describe(Dictionary<string, string> files)
        {
            Receipt result = new Receipt();
            foreach (KeyValuePair<string, string> item in files)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(item.Value);
                using (MemoryStream data = new MemoryStream(bytes)) result.Files.Add(new OwnedFile { Path = item.Key, Length = bytes.Length, Hash = FileProof.Hash(data) });
            }
            return result;
        }
        private static Stream Zip(Dictionary<string, string> files, string extra, bool symbolicLink)
        {
            MemoryStream result = new MemoryStream();
            using (ZipArchive zip = new ZipArchive(result, ZipArchiveMode.Create, true))
            {
                foreach (KeyValuePair<string, string> item in files)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(item.Key);
                    if (symbolicLink && item.Key == "IPMsgDock.exe") entry.ExternalAttributes = unchecked((int)0xa0000000);
                    using (Stream output = entry.Open()) { byte[] bytes = Encoding.UTF8.GetBytes(item.Value); output.Write(bytes, 0, bytes.Length); }
                }
                if (extra != null) using (Stream output = zip.CreateEntry(extra).Open()) output.WriteByte(1);
            }
            result.Position = 0;
            return result;
        }
        private static void Assert(bool condition, string label)
        {
            if (!condition) throw new Exception("FAILED: " + label);
            passed++;
            Console.WriteLine("PASS " + label);
        }
        private static void Reject(Action action, string label)
        {
            try { action(); }
            catch (InvalidDataException) { Assert(true, label); return; }
            catch (IOException) { Assert(true, label); return; }
            catch (ArgumentException) { Assert(true, label); return; }
            throw new Exception("FAILED: " + label + " unexpectedly succeeded");
        }
    }
}
