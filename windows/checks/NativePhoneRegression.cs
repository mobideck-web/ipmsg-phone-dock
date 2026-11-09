using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IPMsg.Dock;

internal static class NativePhoneRegression
{
    private static string Serial;
    private static readonly List<string> results = new List<string>();
    private static string report;
    private static int passes;

    public static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: NativePhoneRegression.exe <runtime-directory> <private-state-directory> <explicit-device-serial>");
            return 2;
        }
        Serial = args[2];
        AdbBridge.ValidateSerial(Serial);
        string state = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(state);
        report = Path.Combine(state, "PhoneRegression-results.txt");
        try { RunAsync(args[0], state).GetAwaiter().GetResult(); Log("PASS: " + passes + " physical backend checks. No GUI or OLE interaction tested."); return 0; }
        catch (Exception error) { Log("FAIL: " + error.GetType().Name + ": " + error.Message); return 1; }
        finally { File.WriteAllLines(report, results, new UTF8Encoding(false)); }
    }

    private static async Task RunAsync(string runtime, string state)
    {
        string staging = Path.Combine(state, "staging");
        string source = Path.Combine(state, "generated-sources");
        Directory.CreateDirectory(source);
        string ownedName = "dock-regression-" + Guid.NewGuid().ToString("N");
        string remote = "/sdcard/Download/IPMsg/" + ownedName;
        AdbBridge bridge = new AdbBridge(runtime, state);
        FileTransferService transfers = new FileTransferService(bridge, staging, 120000);
        StagedFiles staged = new StagedFiles(transfers, state);
        Log("Physical backend validation on the explicitly selected Android phone; isolated ADB 5041; generated files only.");
        Log("Temporary phone folder: a newly generated dock-regression folder in Downloads/IPMsg.");
        using (CancellationTokenSource limit = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
        {
            CancellationToken token = limit.Token;
            CommandResult connected = await bridge.RunAsync(new[] { "-s", Serial, "get-state" }, token, 10000);
            connected.EnsureSuccess("Device connection");
            Check(connected.Output.Trim() == "device", "Authorized device responds on the existing isolated connection");
            await transfers.EnsureInboxAsync(Serial, token);
            bool created = false;
            Exception capturedError = null;
            try
            {
                string creation = "base=$(readlink -f /sdcard/Download/IPMsg) || exit 40\n" +
                    "case \"$base\" in /storage/emulated/0/Download/IPMsg) ;; *) exit 40;; esac\n" +
                    "mkdir \"$base/" + ownedName + "\" || exit 40\n";
                (await bridge.ShellAsync(Serial, creation, token, 10000)).EnsureSuccess("Create owned test folder");
                created = true;
                string empty = Path.Combine(source, "empty.bin");
                string unicode = Path.Combine(source, "IPMsg-é-测试-عربي.txt");
                string large = Path.Combine(source, "generated-32MiB.bin");
                string cancellation = Path.Combine(source, "generated-50MiB-cancel.bin");
                File.WriteAllBytes(empty, new byte[0]);
                File.WriteAllText(unicode, "IPMsg offline transfer check\nFrançais — 中文 — العربية\n", new UTF8Encoding(false));
                Generate(large, 32);
                Generate(cancellation, 50);

                string uploadedEmpty = await TimedUpload(transfers, empty, remote, token);
                string uploadedUnicode = await TimedUpload(transfers, unicode, remote, token);
                string uploadedLarge = await TimedUpload(transfers, large, remote, token);
                string uploadedDuplicate = await TimedUpload(transfers, unicode, remote, token);
                Check(uploadedUnicode != uploadedDuplicate, "Duplicate upload publishes a different name without replacing the original");
                List<PhoneFile> listed = await transfers.ListAsync(Serial, remote, token);
                Check(listed.Count == 4 && listed.All(f => !f.IsDirectory), "Only the four generated complete files are listed");
                PhoneFile emptyFile = Select(listed, uploadedEmpty);
                PhoneFile unicodeFile = Select(listed, uploadedUnicode);
                PhoneFile largeFile = Select(listed, uploadedLarge);
                PhoneFile duplicateFile = Select(listed, uploadedDuplicate);
                string downloadedEmpty = await staged.PrepareAsync(Serial, emptyFile, null, token);
                Check(new FileInfo(downloadedEmpty).Length == 0 && Hash(empty) == Hash(downloadedEmpty), "Empty file round trip is complete and byte-identical");
                string downloadedUnicode = await staged.PrepareAsync(Serial, unicodeFile, null, token);
                Check(Hash(unicode) == Hash(downloadedUnicode) && Path.GetFileName(downloadedUnicode) == Path.GetFileName(unicode),
                    "Unicode filename and Unicode file bytes survive the round trip");
                string downloadedDuplicate = await staged.PrepareAsync(Serial, duplicateFile, null, token);
                Check(Hash(unicode) == Hash(downloadedDuplicate), "Original and uniquely named duplicate retain the same generated contents");
                Stopwatch clock = Stopwatch.StartNew();
                string downloadedLarge = await staged.PrepareAsync(Serial, largeFile, null, token);
                long downloadMs = clock.ElapsedMilliseconds;
                Check(Hash(large) == Hash(downloadedLarge), "32 MiB generated file downloads byte-identically (observed " + downloadMs + " ms; not a benchmark)");
                string ready;
                Check(staged.TryGet(Serial, largeFile, out ready) && ready == downloadedLarge,
                    "Completed large copy is marked ready in the preparation cache");
                clock.Restart();
                string reused = await staged.PrepareAsync(Serial, largeFile, null, token);
                Check(reused == downloadedLarge, "Repeat preparation reuses that same local copy (observed " + clock.ElapsedMilliseconds + " ms)");

                (await bridge.ShellAsync(Serial, "printf changed >> " + AdbBridge.ShellQuote(uploadedUnicode), token, 10000)).EnsureSuccess("Change owned source");
                bool changedRejected = false;
                try { await transfers.DownloadAsync(Serial, unicodeFile, null, token); }
                catch (IOException error) { changedRejected = error.Message.Contains("Refresh the file list"); }
                Check(changedRejected, "A generated phone source changed after listing is refused with a refresh instruction");

                string uploadedCancellation = await TimedUpload(transfers, cancellation, remote, token);
                listed = await transfers.ListAsync(Serial, remote, token);
                PhoneFile cancellationFile = Select(listed, uploadedCancellation);
                using (CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    clock.Restart();
                    Task<string> preparing = staged.PrepareAsync(Serial, cancellationFile, null, cancel.Token);
                    bool observedActiveCopy = await CancelWhenPartialHasBytesAsync(staging, preparing, cancel);
                    bool cancelled = false;
                    try { await preparing; } catch (OperationCanceledException) { cancelled = true; }
                    Check(observedActiveCopy && cancelled, "Preparation cancellation interrupts a real 50 MiB phone pull after local bytes arrive (" + clock.ElapsedMilliseconds + " ms total)");
                    Check(!Directory.GetFiles(staging, "*.part", SearchOption.AllDirectories).Any(), "Cancelled pull leaves no local partial file");
                }
                listed = await transfers.ListAsync(Serial, remote, token);
                Check(Select(listed, uploadedCancellation).Size == 50L * 1024 * 1024 &&
                    !listed.Any(f => f.Name.StartsWith(".ipmsg-part-", StringComparison.Ordinal)),
                    "Cancellation preserves the complete phone source and exposes no upload partials");
            }
            catch (Exception error) { capturedError = error; }
            {
                if (created)
                {
                    // The exact GUID and canonical prefix are checked on the phone before recursive removal.
                    string cleanup = "base=$(readlink -f /sdcard/Download/IPMsg) || exit 40\n" +
                        "[ \"$base\" = /storage/emulated/0/Download/IPMsg ] || exit 40\n" +
                        "owned=\"$base/" + ownedName + "\"\n" +
                        "resolved=$(readlink -f \"$owned\") || exit 40\n" +
                        "[ \"$resolved\" = \"$owned\" ] && [ -d \"$resolved\" ] || exit 40\n" +
                        "case \"$resolved\" in \"$base/" + ownedName + "\") rm -rf -- \"$resolved\";; *) exit 40;; esac\n" +
                        "[ ! -e \"$owned\" ] || exit 40\n";
                    CommandResult removed = await bridge.ShellAsync(Serial, cleanup, CancellationToken.None, 15000);
                    removed.EnsureSuccess("Remove own generated test folder");
                    Log("CLEANUP PASS: exact canonical generated phone folder removed; no personal files accessed.");
                }
            }
            if (capturedError != null) ExceptionDispatchInfo.Capture(capturedError).Throw();
        }
    }

    private static async Task<string> TimedUpload(FileTransferService transfers, string local, string remote, CancellationToken token)
    {
        Stopwatch clock = Stopwatch.StartNew();
        string result = await transfers.UploadAsync(Serial, local, remote, null, token);
        Check(!String.IsNullOrEmpty(result), "Upload complete: " + Path.GetFileName(local) + ", " + new FileInfo(local).Length + " bytes (observed " + clock.ElapsedMilliseconds + " ms)");
        return result;
    }
    private static async Task<bool> CancelWhenPartialHasBytesAsync(string staging, Task copy, CancellationTokenSource cancel)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!copy.IsCompleted && clock.ElapsedMilliseconds < 8000)
        {
            try
            {
                if (Directory.Exists(staging) && Directory.GetFiles(staging, "*.part", SearchOption.AllDirectories).Any(p => new FileInfo(p).Length > 0))
                { cancel.Cancel(); return true; }
            }
            catch (IOException) { }
            await Task.Delay(10).ConfigureAwait(false);
        }
        if (!copy.IsCompleted) cancel.Cancel();
        return false;
    }
    private static PhoneFile Select(IEnumerable<PhoneFile> files, string path)
    {
        string name = path.Substring(path.LastIndexOf('/') + 1);
        return files.Single(f => f.Name == name);
    }
    private static void Generate(string path, int mebibytes)
    {
        Random random = new Random(4127 + mebibytes);
        byte[] block = new byte[1024 * 1024];
        using (FileStream stream = File.Create(path))
            for (int i = 0; i < mebibytes; i++) { random.NextBytes(block); stream.Write(block, 0, block.Length); }
    }
    private static string Hash(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream));
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        passes++; Log("PASS " + passes + ": " + message);
    }
    private static void Log(string text) { results.Add(text); Console.WriteLine(text); }
}
