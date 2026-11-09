using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace IPMsg.Dock.Setup
{
    internal sealed class OwnedFile
    {
        internal string Path;
        internal string Hash;
        internal long Length;
    }

    internal sealed class Receipt
    {
        internal const string FileName = ".ipmsgdock-install.tsv";
        internal readonly List<OwnedFile> Files = new List<OwnedFile>();
        internal readonly List<OwnedFile> Shortcuts = new List<OwnedFile>();

        internal static Receipt Read(Stream stream, bool package)
        {
            Receipt result = new Receipt();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false, true)))
            {
                if (reader.ReadLine() != (package ? "IPMSGDOCK-PAYLOAD-1" : "IPMSGDOCK-INSTALL-1"))
                    throw new InvalidDataException("The installation manifest is not recognized.");
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    if (line.Length > 16384 || result.Files.Count + result.Shortcuts.Count >= 10000)
                        throw new InvalidDataException("The installation manifest is too large.");
                    string[] columns = line.Split('\t');
                    long length;
                    if (columns.Length != 4 || (columns[0] != "F" && (package || columns[0] != "S")) ||
                        !long.TryParse(columns[2], NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0 ||
                        columns[1].Length != 64 || columns[1].Any(delegate(char c) { return !Uri.IsHexDigit(c); }))
                        throw new InvalidDataException("The installation manifest contains an invalid record.");
                    string path = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(columns[3]));
                    if (columns[0] == "F") path = SafePaths.Relative(path);
                    else if (!System.IO.Path.IsPathRooted(path)) throw new InvalidDataException("A shortcut path is not absolute.");
                    if (!names.Add(columns[0] + "|" + path)) throw new InvalidDataException("The installation manifest contains duplicate paths.");
                    OwnedFile record = new OwnedFile { Path = path, Hash = columns[1].ToLowerInvariant(), Length = length };
                    (columns[0] == "F" ? result.Files : result.Shortcuts).Add(record);
                }
            }
            if (!result.Files.Any(delegate(OwnedFile f) { return f.Path.Equals("IPMsgDock.exe", StringComparison.OrdinalIgnoreCase); }) ||
                !result.Files.Any(delegate(OwnedFile f) { return f.Path.Equals("runtime/scrcpy/scrcpy.exe", StringComparison.OrdinalIgnoreCase); }))
                throw new InvalidDataException("The package does not contain the complete IPMsg Dock application.");
            return result;
        }

        internal void Write(string root)
        {
            string path = SafePaths.Under(root, FileName);
            using (FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(file, new UTF8Encoding(false)))
            {
                writer.WriteLine("IPMSGDOCK-INSTALL-1");
                foreach (OwnedFile item in Files) WriteRecord(writer, "F", item);
                foreach (OwnedFile item in Shortcuts) WriteRecord(writer, "S", item);
            }
        }

        private static void WriteRecord(TextWriter writer, string kind, OwnedFile file)
        {
            writer.WriteLine(kind + "\t" + file.Hash + "\t" + file.Length.ToString(CultureInfo.InvariantCulture) + "\t" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Path)));
        }
    }

    internal static class SafePaths
    {
        internal static string Relative(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 220 || value.IndexOf('\\') >= 0 ||
                value.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(value))
                throw new InvalidDataException("The package contains an unsafe path.");
            foreach (string segment in value.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) ||
                    segment.EndsWith(" ", StringComparison.Ordinal) || segment.Any(delegate(char c) { return c < 32 || "<>:\"|?*".IndexOf(c) >= 0; }))
                    throw new InvalidDataException("The package contains an unsafe path.");
                string stem = segment.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                    "123456789\u00b9\u00b2\u00b3".IndexOf(stem[3]) >= 0))
                    throw new InvalidDataException("The package contains a reserved Windows file name.");
            }
            if (value.Equals(Receipt.FileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package attempts to replace its installation receipt.");
            return value;
        }

        internal static string Root(string root)
        {
            if (!Path.IsPathRooted(root)) throw new IOException("The installation directory must be absolute.");
            string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length <= Path.GetPathRoot(full).Length) throw new IOException("A drive root cannot be an installation directory.");
            NoReparse(full);
            return full;
        }

        internal static string Under(string root, string relative)
        {
            // The receipt name is only used by this installer, never by payload paths.
            if (relative != Receipt.FileName) Relative(relative);
            string fullRoot = Root(root);
            string result = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A file path escapes the installation directory.");
            NoReparse(result);
            return result;
        }

        internal static void NoReparse(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Setup cannot use a symbolic link or redirected installation path: " + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                string parent = Path.GetDirectoryName(current);
                if (string.Equals(current, parent, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }

        internal static void MakeParent(string root, string relative)
        {
            string full = Under(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            NoReparse(full);
        }
    }

    internal static class FileProof
    {
        internal static string Hash(Stream stream)
        {
            using (SHA256 algorithm = SHA256.Create()) return Hex(algorithm.ComputeHash(stream));
        }
        internal static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        internal static bool Matches(string path, OwnedFile record)
        {
            SafePaths.NoReparse(path);
            if (!File.Exists(path) || new FileInfo(path).Length != record.Length) return false;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return string.Equals(Hash(stream), record.Hash, StringComparison.OrdinalIgnoreCase);
        }
        internal static OwnedFile Describe(string path, string relative)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return new OwnedFile { Path = relative, Length = stream.Length, Hash = Hash(stream) };
        }
    }

    internal static class InstallerEngine
    {
        internal static void VerifyArchive(Stream archive, Receipt manifest, string extractionRoot)
        {
            Dictionary<string, OwnedFile> expected = manifest.Files.ToDictionary(delegate(OwnedFile f) { return f.Path; }, StringComparer.OrdinalIgnoreCase);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (extractionRoot != null)
            {
                SafePaths.Root(extractionRoot);
                if (Directory.Exists(extractionRoot) && Directory.EnumerateFileSystemEntries(extractionRoot).Any())
                    throw new IOException("The staging directory must be empty.");
                Directory.CreateDirectory(extractionRoot);
            }
            using (ZipArchive zip = new ZipArchive(archive, ZipArchiveMode.Read, false))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string name = entry.FullName;
                    bool directory = name.EndsWith("/", StringComparison.Ordinal);
                    name = SafePaths.Relative(directory ? name.Substring(0, name.Length - 1) : name);
                    // ZIP Unix symlinks and DOS reparse points are never followed or installed.
                    int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
                    if (unixType == 0xa000 || (entry.ExternalAttributes & 0x400) != 0)
                        throw new InvalidDataException("The package contains a link.");
                    if (directory) continue;
                    OwnedFile record;
                    if (!seen.Add(name) || !expected.TryGetValue(name, out record) || entry.Length != record.Length)
                        throw new InvalidDataException("The package and its manifest disagree: " + name);
                    using (Stream input = entry.Open())
                    using (SHA256 hash = SHA256.Create())
                    {
                        FileStream output = null;
                        try
                        {
                            if (extractionRoot != null)
                            {
                                SafePaths.MakeParent(extractionRoot, name);
                                output = new FileStream(SafePaths.Under(extractionRoot, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            }
                            byte[] buffer = new byte[81920];
                            long length = 0;
                            int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                length += count;
                                if (length > record.Length) throw new InvalidDataException("The package expands beyond its declared size.");
                                hash.TransformBlock(buffer, 0, count, null, 0);
                                if (output != null) output.Write(buffer, 0, count);
                            }
                            hash.TransformFinalBlock(new byte[0], 0, 0);
                            if (length != record.Length || !string.Equals(FileProof.Hex(hash.Hash), record.Hash, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("A package file failed its SHA-256 check: " + name);
                        }
                        finally { if (output != null) output.Dispose(); }
                    }
                }
            }
            if (seen.Count != expected.Count) throw new InvalidDataException("The package is missing one or more files.");
        }

        internal static Receipt Previous(string root)
        {
            string path = SafePaths.Under(root, Receipt.FileName);
            if (!File.Exists(path))
            {
                if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                    throw new IOException("The installation directory contains files from an unknown installation. Move those files before installing.");
                return new Receipt();
            }
            using (FileStream stream = File.OpenRead(path)) return Receipt.Read(stream, false);
        }

        internal static void EnsureClosed(string root, IEnumerable<OwnedFile> records)
        {
            foreach (Process process in Process.GetProcessesByName("IPMsgDock"))
            {
                using (process)
                {
                    try
                    {
                        string executable = process.MainModule.FileName;
                        if (string.Equals(Path.GetFullPath(executable), Path.Combine(SafePaths.Root(root), "IPMsgDock.exe"), StringComparison.OrdinalIgnoreCase))
                            throw new IOException("IPMsg Dock is running. Choose Exit from its tray menu, close phone sessions, then try again.");
                    }
                    catch (System.ComponentModel.Win32Exception) { /* File lock checks below still fail safely. */ }
                    catch (InvalidOperationException) { }
                }
            }
            foreach (OwnedFile record in records)
            {
                string file = SafePaths.Under(root, record.Path);
                if (!File.Exists(file)) continue;
                try { using (FileStream locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                catch (IOException) { throw new IOException("An installed file is in use. Exit IPMsg Dock and its phone sessions, then try again: " + record.Path); }
            }
        }

        internal static void InstallFiles(string stage, string root, Receipt package, Action<Receipt, Receipt> integration, Action undoIntegration)
        {
            root = SafePaths.Root(root);
            stage = SafePaths.Root(stage);
            if (stage.Equals(root, StringComparison.OrdinalIgnoreCase) || stage.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The staging and installation directories must be separate.");
            Receipt previous = Previous(root);
            Dictionary<string, OwnedFile> owned = previous.Files.ToDictionary(delegate(OwnedFile f) { return f.Path; }, StringComparer.OrdinalIgnoreCase);
            foreach (OwnedFile item in package.Files)
            {
                string staged = SafePaths.Under(stage, item.Path);
                if (!FileProof.Matches(staged, item)) throw new IOException("A staged file changed before installation: " + item.Path);
                string target = SafePaths.Under(root, item.Path);
                if (File.Exists(target) && !owned.ContainsKey(item.Path))
                    throw new IOException("Setup would overwrite an unrelated file: " + item.Path);
            }
            foreach (OwnedFile item in previous.Files)
            {
                string target = SafePaths.Under(root, item.Path);
                if (File.Exists(target) && !FileProof.Matches(target, item))
                    throw new IOException("An installed file has been modified. Setup preserved it; move it before updating: " + item.Path);
            }
            EnsureClosed(root, previous.Files);
            string backup = stage + "-rollback";
            SafePaths.Root(backup);
            if (Directory.Exists(backup) || File.Exists(backup)) throw new IOException("The rollback directory already exists.");
            Directory.CreateDirectory(backup);
            List<string> backed = new List<string>();
            List<string> installed = new List<string>();
            bool cleanupBackup = false;
            List<string> backupCandidates = previous.Files.Select(delegate(OwnedFile f) { return f.Path; }).Concat(new[] { Receipt.FileName }).ToList();
            try
            {
                Directory.CreateDirectory(root);
                foreach (string relative in backupCandidates)
                {
                    string old = SafePaths.Under(root, relative);
                    if (!File.Exists(old)) continue;
                    SafePaths.MakeParent(backup, relative);
                    File.Move(old, SafePaths.Under(backup, relative));
                    backed.Add(relative);
                }
                foreach (OwnedFile item in package.Files)
                {
                    SafePaths.MakeParent(root, item.Path);
                    File.Move(SafePaths.Under(stage, item.Path), SafePaths.Under(root, item.Path));
                    installed.Add(item.Path);
                }
                Receipt receipt = new Receipt();
                receipt.Files.AddRange(package.Files);
                if (integration != null) integration(previous, receipt);
                receipt.Write(root);
                cleanupBackup = true;
            }
            catch
            {
                Exception integrationError = null;
                try { if (undoIntegration != null) undoIntegration(); }
                catch (Exception error) { integrationError = error; }
                // Delete only names successfully moved by this transaction. Never sweep a directory.
                foreach (string relative in installed.AsEnumerable().Reverse())
                {
                    string path = SafePaths.Under(root, relative);
                    if (File.Exists(path)) File.Delete(path);
                }
                string receiptPath = SafePaths.Under(root, Receipt.FileName);
                if (File.Exists(receiptPath)) File.Delete(receiptPath);
                foreach (string relative in backed)
                {
                    SafePaths.MakeParent(root, relative);
                    File.Move(SafePaths.Under(backup, relative), SafePaths.Under(root, relative));
                }
                RemoveEmptyDirectories(root, installed);
                cleanupBackup = true;
                if (integrationError != null) throw new IOException("Application files were restored, but a Windows shortcut or registry change could not be restored.", integrationError);
                throw;
            }
            // If rollback itself fails, retain the old files in the named rollback directory.
            finally { if (cleanupBackup) CleanupOwnedTree(backup, backupCandidates); }
        }

        internal static List<string> RemoveInstalledFiles(string root, Receipt receipt)
        {
            EnsureClosed(root, receipt.Files);
            List<string> preserved = new List<string>();
            foreach (OwnedFile item in receipt.Files.OrderBy(delegate(OwnedFile f) { return f.Path.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0; }))
            {
                string file = SafePaths.Under(root, item.Path);
                if (!File.Exists(file)) continue;
                if (!FileProof.Matches(file, item)) { preserved.Add(item.Path); continue; }
                File.Delete(file);
            }
            return preserved;
        }

        internal static void CleanupOwnedTree(string root, IEnumerable<string> relatives)
        {
            if (!Directory.Exists(root)) return;
            root = SafePaths.Root(root);
            List<string> names = relatives.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string relative in names)
            {
                string file = SafePaths.Under(root, relative);
                if (File.Exists(file)) File.Delete(file);
            }
            RemoveEmptyDirectories(root, names);
        }

        internal static void RemoveEmptyDirectories(string root, IEnumerable<string> relatives)
        {
            HashSet<string> directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            root = SafePaths.Root(root);
            foreach (string relative in relatives)
            {
                string directory = Path.GetDirectoryName(SafePaths.Under(root, relative));
                while (!string.IsNullOrEmpty(directory) && !directory.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    directories.Add(directory);
                    directory = Path.GetDirectoryName(directory);
                }
            }
            foreach (string directory in directories.OrderByDescending(delegate(string d) { return d.Length; }).Concat(new[] { root }))
            {
                SafePaths.NoReparse(directory);
                if (!Directory.Exists(directory)) continue;
                try { Directory.Delete(directory, false); }
                catch (IOException) { /* Preserve folders containing any untracked or changed files. */ }
            }
        }
    }
}
