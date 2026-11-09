using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IPMsg.Dock
{
    public sealed class PhoneFile
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public long Size { get; set; }
        public bool IsDirectory { get; set; }
        public DateTime ModifiedUtc { get; set; }
    }

    public sealed class TransferProgress
    {
        public string Name { get; set; }
        public string Direction { get; set; }
        public string Message { get; set; }
        public long Bytes { get; set; }
        public long Total { get; set; }
        public double BytesPerSecond { get; set; }
    }

    /// <summary>Explicit file copies over the authorized, isolated ADB connection.</summary>
    public sealed class FileTransferService
    {
        public const string InboxPath = "/sdcard/Download/IPMsg";
        private const int CopyTimeoutMs = 60 * 60 * 1000;
        private readonly AdbBridge bridge;
        private readonly string cacheDirectory;
        private readonly int copyTimeoutMs;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public FileTransferService(AdbBridge bridge, string cacheDirectory, int copyTimeoutMs = CopyTimeoutMs)
        {
            if (bridge == null) throw new ArgumentNullException("bridge");
            if (String.IsNullOrWhiteSpace(cacheDirectory)) throw new ArgumentException("A staging folder is required.");
            this.bridge = bridge;
            this.cacheDirectory = Path.GetFullPath(cacheDirectory);
            if (copyTimeoutMs < 1 || copyTimeoutMs > CopyTimeoutMs) throw new ArgumentOutOfRangeException("copyTimeoutMs");
            this.copyTimeoutMs = copyTimeoutMs;
        }

        // Resolve on the phone for every operation, including paths returned by a previous listing.
        // A symlink inside shared storage must never grant access outside it.
        private static string PathScript(string path, bool directory)
        {
            ValidatePath(path);
            return "base=$(readlink -f /sdcard) || exit 40\n" +
                "[ -n \"$base\" ] && [ -d \"$base\" ] || { echo IPMSG_ERROR_STORAGE >&2; exit 40; }\n" +
                "p=$(readlink -f " + AdbBridge.ShellQuote(path) + ") || exit 40\n" +
                "case \"$p\" in \"$base\"|\"$base\"/*) ;; *) echo IPMSG_ERROR_PATH >&2; exit 40;; esac\n" +
                (directory ? "[ -d \"$p\" ]" : "[ -f \"$p\" ]") +
                " || { echo IPMSG_ERROR_MISSING >&2; exit 40; }\n";
        }

        public async Task EnsureInboxAsync(string serial, CancellationToken token)
        {
            ValidateSerial(serial);
            string script = PathScript("/sdcard", true) +
                "mkdir -p \"$p/Download\" || exit 40\n" +
                "d=$(readlink -f \"$p/Download\") || exit 40\n" +
                "case \"$d\" in \"$base\"/*) ;; *) echo IPMSG_ERROR_PATH >&2; exit 40;; esac\n" +
                "mkdir -p \"$d/IPMsg\" || exit 40\n" +
                "d=$(readlink -f \"$d/IPMsg\") || exit 40\n" +
                "case \"$d\" in \"$base\"/*) ;; *) echo IPMSG_ERROR_PATH >&2; exit 40;; esac\n" +
                "[ -d \"$d\" ] || exit 40\n";
            Check(await bridge.ShellAsync(serial, script, token).ConfigureAwait(false), "Create the phone's IPMsg folder");
        }

        public async Task<List<PhoneFile>> ListAsync(string serial, string path, CancellationToken token)
        {
            ValidateSerial(serial);
            string script = PathScript(path, true) +
                "n=0\n" +
                "for f in \"$p\"/* \"$p\"/.[!.]* \"$p\"/..?*; do\n" +
                " [ -e \"$f\" ] || continue\n" +
                " name=${f##*/}\n" +
                " case \"$name\" in .ipmsg-part-*) continue;; esac\n" +
                " c=$(readlink -f \"$f\") || continue\n" +
                " case \"$c\" in \"$base\"|\"$base\"/*) ;; *) continue;; esac\n" +
                " if [ -d \"$f\" ]; then t=d; elif [ -f \"$f\" ]; then t=f; else continue; fi\n" +
                " info=$(stat -c '%s %Y' \"$c\") || continue\n" +
                " hex=$(printf '%s' \"$name\" | od -An -tx1 | tr -d ' \\n')\n" +
                " set -- $info\n" +
                " printf '%s\\t%s\\t%s\\t%s\\n' \"$t\" \"$hex\" \"$1\" \"$2\"\n" +
                " n=$((n+1)); [ \"$n\" -le 5000 ] || { echo IPMSG_ERROR_TOO_MANY >&2; exit 40; }\n" +
                "done\n";
            CommandResult result = await bridge.ShellAsync(serial, script, token, 90000).ConfigureAwait(false);
            Check(result, "Read the phone folder");
            if (result.OutputTruncated)
                throw new IOException("This folder contains too many items to display. Choose a smaller folder.");
            List<PhoneFile> files = new List<PhoneFile>();
            foreach (string line in Lines(result.Output))
            {
                string[] parts = line.Split('\t');
                long size, modified;
                if (parts.Length != 4 || (parts[0] != "d" && parts[0] != "f") ||
                    !Int64.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 0 ||
                    !Int64.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out modified))
                    throw new IOException("The phone returned an incomplete folder listing. Refresh and try again.");
                string name = DecodeHex(parts[1]);
                if (!ValidName(name)) continue;
                DateTime modifiedUtc;
                try { modifiedUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(modified); }
                catch (ArgumentOutOfRangeException) { modifiedUtc = DateTime.MinValue; }
                files.Add(new PhoneFile { Name = name, FullPath = path.TrimEnd('/') + "/" + name,
                    Size = size, IsDirectory = parts[0] == "d", ModifiedUtc = modifiedUtc });
            }
            return files.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<string> UploadAsync(string serial, string localPath, string remoteDirectory,
            IProgress<TransferProgress> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ValidateSerial(serial);
            ValidatePath(remoteDirectory);
            localPath = Path.GetFullPath(localPath);
            string name = Path.GetFileName(localPath);
            if (!ValidName(name)) throw new IOException("This filename is not supported. Rename it before sending.");
            if (StrictUtf8.GetByteCount(name) > 240) throw new IOException("The filename is too long for the phone. Rename it before sending.");
            string temporaryPath = null;
            bool published = false;
            // This shared-read handle prevents normal Windows writers/deleters changing the source during adb push.
            using (FileStream source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                token.ThrowIfCancellationRequested();
                long size = source.Length;
                DateTime modified = File.GetLastWriteTimeUtc(localPath);
                Report(progress, name, "Upload", "Preparing", 0, size, 0);
                string temporaryName = ".ipmsg-part-" + Guid.NewGuid().ToString("N");
                string script = PathScript(remoteDirectory, true) +
                    "line=$(df -k \"$p\" | tail -n 1); set -- $line; free=$4\n" +
                    "case \"$free\" in ''|*[!0-9]*) ;; *) [ \"$free\" -ge " +
                    ((size / 1024) + 4097).ToString(CultureInfo.InvariantCulture) +
                    " ] || { echo IPMSG_ERROR_SPACE >&2; exit 41; };; esac\n" +
                    "tmp=\"$p/" + temporaryName + "\"\n" +
                    "(set -C; : > \"$tmp\") || { echo IPMSG_ERROR_CREATE >&2; exit 40; }\n" +
                    "printf '%s' \"$tmp\" | od -An -tx1 | tr -d ' \\n'\n";
                temporaryPath = remoteDirectory.TrimEnd('/') + "/" + temporaryName;
                Exception failure = null;
                string destination = null;
                try
                {
                    CommandResult prepared = await bridge.ShellAsync(serial, script, token).ConfigureAwait(false);
                    Check(prepared, "Prepare the phone destination");
                    temporaryPath = DecodeHex(prepared.Output.Trim());
                    ValidatePath(temporaryPath);
                    using (CancellationTokenSource polling = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        Task poll = IgnorePollingAsync(PollUploadAsync(serial, temporaryPath, name, size, progress, polling.Token));
                        try
                        {
                            CommandResult copy = await bridge.RunAsync(new[] { "-s", serial, "push", localPath, temporaryPath },
                                token, copyTimeoutMs).ConfigureAwait(false);
                            Check(copy, "Send the file");
                        }
                        finally { polling.Cancel(); }
                        await poll.ConfigureAwait(false);
                    }
                    token.ThrowIfCancellationRequested();
                    if (source.Length != size || File.GetLastWriteTimeUtc(localPath) != modified)
                        throw new IOException("The source file changed during copying. Send it again.");
                    RemoteStat copied = await StatAsync(serial, temporaryPath, token).ConfigureAwait(false);
                    if (copied.Size != size) throw new IOException("The phone received an incomplete file. Send it again.");
                    Report(progress, name, "Upload", "Finishing", size, size, 0);
                    // mv -n never intentionally replaces an existing destination. Verify that the private
                    // temporary file disappeared; a skipped no-clobber move is not a successful transfer.
                    string stem = Path.GetFileNameWithoutExtension(name);
                    string extension = Path.GetExtension(name);
                    script = PathScript(remoteDirectory, true) +
                        "tmp=" + AdbBridge.ShellQuote(temporaryPath) + "\n" +
                        "[ -f \"$tmp\" ] && [ ! -L \"$tmp\" ] || exit 40\n" +
                        "[ \"$(stat -c %s \"$tmp\")\" = " + size.ToString(CultureInfo.InvariantCulture) + " ] || exit 40\n" +
                        "i=0\nwhile [ \"$i\" -lt 10000 ]; do\n" +
                        " if [ \"$i\" -eq 0 ]; then name=" + AdbBridge.ShellQuote(name) + "; else name=" +
                        AdbBridge.ShellQuote(stem) + "\" ($i)\"" + AdbBridge.ShellQuote(extension) + "; fi\n" +
                        " dst=\"$p/$name\"\n" +
                        " if [ ! -e \"$dst\" ] && [ ! -L \"$dst\" ]; then\n" +
                        "  mv -n \"$tmp\" \"$dst\" || exit 40\n" +
                        "  if [ ! -e \"$tmp\" ]; then\n" +
                        "   [ -f \"$dst\" ] && [ \"$(stat -c %s \"$dst\")\" = " + size.ToString(CultureInfo.InvariantCulture) + " ] || exit 40\n" +
                        "   printf '%s' \"$dst\" | od -An -tx1 | tr -d ' \\n'; exit 0\n" +
                        "  fi\n fi\n i=$((i+1))\ndone\necho IPMSG_ERROR_COLLISION >&2; exit 40\n";
                    CommandResult publication = await bridge.ShellAsync(serial, script, token, 90000).ConfigureAwait(false);
                    Check(publication, "Finish saving the phone file");
                    destination = DecodeHex(publication.Output.Trim());
                    ValidatePath(destination);
                    published = true;
                    Report(progress, name, "Upload", "Complete", size, size, 0);
                }
                catch (Exception ex) { failure = ex; }
                if (!published && temporaryPath != null)
                    await RemoveOwnTemporaryAsync(serial, temporaryPath, temporaryName).ConfigureAwait(false);
                if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
                return destination;
            }
        }

        public async Task<string> DownloadAsync(string serial, PhoneFile file, IProgress<TransferProgress> progress,
            CancellationToken token)
        {
            ValidateSerial(serial);
            if (file == null) throw new ArgumentNullException("file");
            if (file.IsDirectory) throw new IOException("Select individual files. Folder transfers are not supported.");
            ValidatePath(file.FullPath);
            long selectedSize = file.Size;
            DateTime selectedModified = file.ModifiedUtc;
            RemoteStat before = await StatAsync(serial, file.FullPath, token).ConfigureAwait(false);
            long selectedSeconds = (selectedModified.Ticks - 621355968000000000L) / TimeSpan.TicksPerSecond;
            if (before.Size != selectedSize ||
                (selectedModified != DateTime.MinValue && before.ModifiedSeconds != selectedSeconds))
                throw new IOException("The phone file changed since it was listed. Refresh the file list and select it again.");
            string name = Path.GetFileName(file.FullPath);
            if (!ValidName(name)) throw new IOException("This filename is not supported.");
            Directory.CreateDirectory(cacheDirectory);
            DriveInfo disk = new DriveInfo(Path.GetPathRoot(cacheDirectory));
            if (before.Size > Math.Max(0, disk.AvailableFreeSpace - 4L * 1024 * 1024))
                throw new IOException("Windows needs more free disk space to receive this file.");
            string operationDirectory = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(operationDirectory);
            string partial = Path.Combine(operationDirectory, ".ipmsg-download.part");
            string destination = Path.Combine(operationDirectory, SafeWindowsName(name));
            bool completed = false;
            try
            {
                Report(progress, name, "Download", "Preparing", 0, before.Size, 0);
                using (CancellationTokenSource polling = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    Task poll = IgnorePollingAsync(PollDownloadAsync(partial, name, before.Size, progress, polling.Token));
                    try
                    {
                        CommandResult copy = await bridge.RunAsync(new[] { "-s", serial, "pull", file.FullPath, partial },
                            token, copyTimeoutMs).ConfigureAwait(false);
                        Check(copy, "Receive the file");
                    }
                    finally { polling.Cancel(); }
                    await poll.ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                if (!File.Exists(partial) || new FileInfo(partial).Length != before.Size)
                    throw new IOException("The file ended before its advertised size. Reconnect and try again.");
                RemoteStat after = await StatAsync(serial, file.FullPath, token).ConfigureAwait(false);
                if (before.Size != after.Size || before.ModifiedSeconds != after.ModifiedSeconds)
                    throw new IOException("The phone file changed during copying. Select it again and retry.");
                Report(progress, name, "Download", "Finishing", before.Size, before.Size, 0);
                // Same-directory rename exposes only a complete file. File.Move never overwrites.
                File.Move(partial, destination);
                if (new FileInfo(destination).Length != before.Size) throw new IOException("The saved file could not be verified.");
                completed = true;
                Report(progress, name, "Download", "Complete", before.Size, before.Size, 0);
                return destination;
            }
            finally
            {
                if (!completed)
                {
                    TryDeleteFile(partial);
                    // This operation owns both names inside a freshly created GUID folder.
                    TryDeleteFile(destination);
                    try { Directory.Delete(operationDirectory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }

        private async Task<RemoteStat> StatAsync(string serial, string path, CancellationToken token)
        {
            CommandResult result = await bridge.ShellAsync(serial, PathScript(path, false) + "stat -c '%s %Y' \"$p\"\n", token, 15000).ConfigureAwait(false);
            Check(result, "Check the phone file");
            string[] parts = result.Output.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            long size, modified;
            if (parts.Length != 2 || !Int64.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 0 ||
                !Int64.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out modified))
                throw new IOException("The phone could not verify this file. Refresh and retry.");
            return new RemoteStat { Size = size, ModifiedSeconds = modified };
        }

        private async Task PollUploadAsync(string serial, string path, string name, long total,
            IProgress<TransferProgress> progress, CancellationToken token)
        {
            Stopwatch clock = Stopwatch.StartNew();
            long previous = 0;
            double previousTime = 0;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                try
                {
                    RemoteStat current = await StatAsync(serial, path, token).ConfigureAwait(false);
                    long bytes = Math.Min(total, current.Size);
                    double now = clock.Elapsed.TotalSeconds;
                    Report(progress, name, "Upload", "Sending", bytes, total, Math.Max(0, bytes - previous) / Math.Max(.001, now - previousTime));
                    previous = bytes; previousTime = now;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* The copy command remains the authority on connection failure. */ }
            }
        }

        private static async Task PollDownloadAsync(string path, string name, long total,
            IProgress<TransferProgress> progress, CancellationToken token)
        {
            Stopwatch clock = Stopwatch.StartNew();
            long previous = 0;
            double previousTime = 0;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(500, token).ConfigureAwait(false);
                try
                {
                    long bytes = File.Exists(path) ? Math.Min(total, new FileInfo(path).Length) : 0;
                    double now = clock.Elapsed.TotalSeconds;
                    Report(progress, name, "Download", "Receiving", bytes, total, Math.Max(0, bytes - previous) / Math.Max(.001, now - previousTime));
                    previous = bytes; previousTime = now;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private async Task RemoveOwnTemporaryAsync(string serial, string path, string ownedName)
        {
            if (!String.Equals(path.Substring(path.LastIndexOf('/') + 1), ownedName, StringComparison.Ordinal) ||
                !ownedName.StartsWith(".ipmsg-part-", StringComparison.Ordinal)) return;
            try
            {
                string parent = path.Substring(0, path.LastIndexOf('/'));
                await bridge.ShellAsync(serial, PathScript(parent, true) +
                    "rm -f \"$p/" + ownedName + "\"\n", CancellationToken.None, 5000).ConfigureAwait(false);
            }
            catch (Exception) { /* Disconnected phones may retain this uniquely named hidden partial. */ }
        }

        private static async Task IgnorePollingAsync(Task task)
        {
            try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (IOException) { }
        }

        private static void Report(IProgress<TransferProgress> progress, string name, string direction, string message,
            long bytes, long total, double speed)
        {
            if (progress != null) progress.Report(new TransferProgress { Name = name, Direction = direction,
                Message = message, Bytes = bytes, Total = total, BytesPerSecond = speed });
        }

        private static void Check(CommandResult result, string operation)
        {
            if (result.ExitCode == 0) return;
            string detail = (result.Error ?? "") + "\n" + (result.Output ?? "");
            if (detail.Contains("IPMSG_ERROR_SPACE") || detail.IndexOf("No space left", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new IOException("The phone needs more free storage. Free space and retry.");
            if (detail.Contains("IPMSG_ERROR_PATH")) throw new IOException("Only the phone's shared storage is available.");
            if (detail.Contains("IPMSG_ERROR_MISSING")) throw new IOException("This phone file or folder is no longer available. Refresh and try again.");
            if (detail.Contains("IPMSG_ERROR_TOO_MANY")) throw new IOException("This folder contains too many items. Choose a smaller folder.");
            if (detail.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new IOException("Unlock the phone and approve this computer's USB debugging connection.");
            if (detail.IndexOf("offline", StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                detail.IndexOf("disconnected", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new IOException("The phone disconnected. Reconnect it, then retry the file.");
            if (detail.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new IOException("Android denied access to this location. Choose a file in Downloads, Pictures or DCIM.");
            throw new IOException(operation + " failed. Reconnect the phone and try again.");
        }

        private static void ValidateSerial(string serial)
        {
            if (String.IsNullOrWhiteSpace(serial) || serial.Any(Char.IsControl)) throw new ArgumentException("Select an authorized phone.");
        }

        private static void ValidatePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(Char.IsControl) || path.IndexOf('\\') >= 0 ||
                !(path == "/sdcard" || path.StartsWith("/sdcard/", StringComparison.Ordinal) ||
                  path == "/storage/emulated/0" || path.StartsWith("/storage/emulated/0/", StringComparison.Ordinal)) ||
                path.Split('/').Any(s => s == ".." || s == "."))
                throw new IOException("Choose a location inside the phone's shared storage.");
        }

        private static bool ValidName(string name)
        {
            return !String.IsNullOrWhiteSpace(name) && name != "." && name != ".." &&
                name.IndexOf('/') < 0 && name.IndexOf('\\') < 0 && !name.Any(Char.IsControl);
        }

        private static string DecodeHex(string hex)
        {
            if (hex.Length == 0 || hex.Length > 8192 || (hex.Length & 1) != 0) throw new IOException("The phone returned an invalid file path.");
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                if (!Byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                    throw new IOException("The phone returned an invalid file path.");
            try { return StrictUtf8.GetString(bytes); } catch (DecoderFallbackException) { throw new IOException("This filename is not valid UTF-8."); }
        }

        private static IEnumerable<string> Lines(string text)
        {
            return (text ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'));
        }

        private static string SafeWindowsName(string name)
        {
            return VirtualFileDrag.ExportName(name);
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private sealed class RemoteStat
        {
            public long Size;
            public long ModifiedSeconds;
        }
    }
}
