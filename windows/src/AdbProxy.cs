using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IPMsg.Dock
{
    // scrcpy's native APK drop normally installs the APK. In the dock, every user drop is a
    // verified shared-storage copy instead. Normal scrcpy ADB traffic keeps its binary pipes.
    public static class AdbProxy
    {
        public static bool IsInvocation
        {
            get { return Environment.GetEnvironmentVariable("IPMSG_DOCK_ADB_PROXY") == "1"; }
        }

        public static int Run(string[] args)
        {
            string status = null;
            try
            {
                string runtime = Required("IPMSG_DOCK_PROXY_RUNTIME");
                string state = Required("IPMSG_DOCK_PROXY_STATE");
                string expectedSerial = Required("IPMSG_DOCK_PROXY_SERIAL");
                AdbBridge.ValidateSerial(expectedSerial);
                status = Path.GetFullPath(Required("IPMSG_DOCK_PROXY_STATUS"));
                string sessions = Path.Combine(Path.GetFullPath(state), "sessions") + Path.DirectorySeparatorChar;
                if (!status.StartsWith(sessions, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(status) + Path.DirectorySeparatorChar != sessions)
                    throw new IOException("Invalid private dock session.");
                if (File.Exists(status + ".cancel")) throw new OperationCanceledException();
                string serial;
                int commandIndex = ParseGlobalArguments(args, expectedSerial, out serial);
                string verb = args[commandIndex];
                AdbBridge bridge = new AdbBridge(runtime, state);
                if (verb == "push")
                {
                    List<string> operands = PushOperands(args, commandIndex + 1);
                    if (operands.Count != 2) throw new IOException("Drop individual files. Folder and multi-source ADB drops are not supported.");
                    string source = Path.GetFullPath(operands[0]);
                    string server = Path.Combine(Path.GetFullPath(runtime), "scrcpy-server");
                    if (String.Equals(source, server, StringComparison.OrdinalIgnoreCase) &&
                        operands[1] == "/data/local/tmp/scrcpy-server.jar" && File.Exists(server))
                        return Forward(bridge, args, commandIndex, serial, status);
                    return CopyToPhone(bridge, serial, source, status);
                }
                if (verb == "install" || verb == "install-multiple" || verb == "install-multi-package")
                {
                    string source = InstallSource(args, commandIndex + 1);
                    return CopyToPhone(bridge, serial, source, status);
                }
                if (verb.StartsWith("install", StringComparison.Ordinal) || verb == "uninstall")
                    throw new IOException("Installing or removing apps through a dock drop is disabled. APK files are copied only.");
                if (!AllowedVerb(verb)) throw new IOException("Unsupported dock ADB operation: " + Clean(verb));
                return Forward(bridge, args, commandIndex, serial, status);
            }
            catch (OperationCanceledException)
            {
                WriteStatus(status, "COPY_ERROR\tCancelled");
                return 1;
            }
            catch (Exception error)
            {
                WriteStatus(status, "COPY_ERROR\t" + Clean(error.Message));
                try { Console.Error.WriteLine("IPMsg Dock: " + error.Message); } catch (IOException) { }
                return 1;
            }
        }

        private static int ParseGlobalArguments(string[] args, string expectedSerial, out string serial)
        {
            serial = Environment.GetEnvironmentVariable("ANDROID_SERIAL");
            if (String.IsNullOrEmpty(serial)) serial = expectedSerial;
            int index = 0;
            while (index < args.Length && args[index].StartsWith("-", StringComparison.Ordinal))
            {
                string option = args[index++];
                if (option == "-s")
                {
                    if (index >= args.Length) throw new ArgumentException("Missing phone identifier.");
                    serial = args[index++];
                }
                else if (option == "-P" || option == "-H" || option == "-L")
                {
                    if (index >= args.Length) throw new ArgumentException("Missing ADB server option.");
                    // Always forward to our own localhost server; never accept another host or port.
                    string value = args[index++];
                    if ((option == "-P" && value != AdbBridge.ServerPort.ToString()) ||
                        (option == "-H" && value != "localhost" && value != "127.0.0.1") ||
                        (option == "-L" && value != "tcp:127.0.0.1:" + AdbBridge.ServerPort))
                        throw new ArgumentException("The dock uses its own local Android connection.");
                }
                else throw new ArgumentException("Unsupported ADB selection option.");
            }
            if (index >= args.Length) throw new ArgumentException("An ADB operation is required.");
            if (serial != expectedSerial) throw new ArgumentException("The drop does not belong to the connected phone.");
            AdbBridge.ValidateSerial(serial);
            return index;
        }

        private static List<string> PushOperands(string[] args, int index)
        {
            List<string> result = new List<string>();
            bool options = true;
            for (; index < args.Length; index++)
            {
                string value = args[index];
                if (options && value == "--") { options = false; continue; }
                if (options && (value == "-p" || value == "-Z" || value == "--sync")) continue;
                if (options && value == "-z")
                {
                    if (++index >= args.Length) throw new ArgumentException("Missing compression option.");
                    continue;
                }
                if (options && value.StartsWith("-", StringComparison.Ordinal)) throw new ArgumentException("Unsupported drop option.");
                options = false;
                result.Add(value);
            }
            return result;
        }

        private static string InstallSource(string[] args, int index)
        {
            string source = null;
            for (; index < args.Length; index++)
            {
                string value = args[index];
                if (value == "-r" || value == "-t" || value == "-d" || value == "-g" || value == "--streaming" ||
                    value == "--no-streaming" || value == "--fastdeploy" || value == "--no-fastdeploy" || value == "--") continue;
                if (value.StartsWith("-", StringComparison.Ordinal) || source != null || !File.Exists(value))
                    throw new IOException("Drop one APK file at a time. APK drops are copied, never installed.");
                source = Path.GetFullPath(value);
            }
            if (source == null) throw new IOException("No readable file was supplied for copying.");
            return source;
        }

        private static bool AllowedVerb(string verb)
        {
            switch (verb)
            {
                case "start-server": case "version": case "devices": case "get-state": case "get-serialno":
                case "get-devpath": case "forward": case "reverse": case "shell": case "exec-out": case "exec-in":
                case "host-features": case "features": case "server-status": case "wait-for-device":
                    return true;
                default: return false;
            }
        }

        private static int Forward(AdbBridge bridge, string[] args, int commandIndex, string serial, string status)
        {
            StringBuilder command = new StringBuilder("-P " + AdbBridge.ServerPort + " -s " + AdbBridge.QuoteArgument(serial));
            for (int index = commandIndex; index < args.Length; index++) command.Append(' ').Append(AdbBridge.QuoteArgument(args[index]));
            ProcessStartInfo start = new ProcessStartInfo(bridge.AdbPath, command.ToString());
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardError = true;
            start.WorkingDirectory = Path.GetDirectoryName(bridge.AdbPath);
            // Redirecting one stream makes .NET Framework explicitly set STARTF_USESTDHANDLES.
            // stdin/stdout retain the inherited native pipes; stderr is copied byte-for-byte.
            bridge.ConfigureEnvironment(start);
            start.EnvironmentVariables["IPMSG_DOCK_ADB_PROXY"] = "0";
            Process child = Process.Start(start);
            Task errors = null;
            try
            {
                if (child == null) throw new IOException("Could not start the Android connection.");
                errors = PumpErrorAsync(child.StandardError.BaseStream, Console.OpenStandardError());
                bool cancelled = false;
                while (!child.WaitForExit(200))
                {
                    if (File.Exists(status + ".cancel"))
                    {
                        cancelled = true;
                        AdbBridge.KillOwned(child);
                        child.WaitForExit(1500);
                        break;
                    }
                }
                // A daemon retaining a pipe handle must not keep this short-lived proxy alive.
                Task drained = Task.WhenAny(errors, Task.Delay(2000)).GetAwaiter().GetResult();
                if (drained == errors) errors.GetAwaiter().GetResult();
                return cancelled ? 1 : child.ExitCode;
            }
            finally
            {
                if (child != null)
                {
                    // Framework stream disposal may wait for the outstanding pipe read.
                    // Let only a worker dispose after the inherited handle is released.
                    if (errors == null || errors.IsCompleted) child.Dispose();
                    else errors.ContinueWith(delegate(Task completed) { var ignored = completed.Exception; child.Dispose(); }, TaskScheduler.Default);
                }
            }
        }

        private static async Task PumpErrorAsync(Stream source, Stream destination)
        {
            byte[] buffer = new byte[8192];
            bool writable = true;
            try
            {
                while (true)
                {
                    int count = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (count == 0) return;
                    if (!writable) continue;
                    try
                    {
                        await destination.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                        await destination.FlushAsync().ConfigureAwait(false);
                    }
                    catch (IOException) { writable = false; }
                    catch (ObjectDisposedException) { writable = false; }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { destination.Dispose(); }
        }

        private static int CopyToPhone(AdbBridge bridge, string serial, string source, string status)
        {
            if (!File.Exists(source)) throw new IOException("Drop an existing individual file; folders are not supported.");
            string copyGeneration;
            if (!ReadCopyGeneration(status, out copyGeneration)) copyGeneration = "";
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            using (Timer watcher = new Timer(delegate
            {
                string currentGeneration;
                bool copyCancelled = ReadCopyGeneration(status, out currentGeneration) && currentGeneration != copyGeneration;
                if (File.Exists(status + ".cancel") || copyCancelled)
                {
                    try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
                }
            }, null, 0, 200))
            {
                FileStream copyLock = null;
                try
                {
                    while (copyLock == null)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        try { copyLock = new FileStream(status + ".copylock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                        catch (IOException) { cancellation.Token.WaitHandle.WaitOne(200); }
                    }
                    FileTransferService files = new FileTransferService(bridge, Path.Combine(bridge.StateDirectory, "drag-cache"));
                    files.EnsureInboxAsync(serial, cancellation.Token).GetAwaiter().GetResult();
                    string destination = files.UploadAsync(serial, source, FileTransferService.InboxPath,
                        new InlineProgress(delegate(TransferProgress progress)
                        {
                            WriteStatus(status, "COPY_PROGRESS\t" + Clean(progress.Name) + "\t" + progress.Bytes + "\t" + progress.Total + "\t" + Clean(progress.Message));
                        }), cancellation.Token).GetAwaiter().GetResult();
                    WriteStatus(status, "COPY_OK\t" + Clean(destination));
                    try { Console.Out.WriteLine("Copied to " + destination + " (not installed)."); } catch (IOException) { }
                    return 0;
                }
                finally { if (copyLock != null) copyLock.Dispose(); }
            }
        }

        private sealed class InlineProgress : IProgress<TransferProgress>
        {
            private readonly Action<TransferProgress> report;
            public InlineProgress(Action<TransferProgress> report) { this.report = report; }
            public void Report(TransferProgress value) { report(value); }
        }

        // A cancellation generation affects copies already in flight or queued. A future drop
        // snapshots the new value, so cancelling one copy never revokes the live screen session.
        private static bool ReadCopyGeneration(string status, out string generation)
        {
            generation = "";
            try
            {
                if (!File.Exists(status + ".copycancel")) return true;
                using (FileStream stream = new FileStream(status + ".copycancel", FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(stream)) generation = reader.ReadToEnd();
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static string Required(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (String.IsNullOrWhiteSpace(value)) throw new IOException("Missing private dock session configuration.");
            return value;
        }

        private static string Clean(string value)
        {
            if (value == null) return "";
            StringBuilder text = new StringBuilder();
            foreach (char c in value) if (!Char.IsControl(c)) { text.Append(c); if (text.Length >= 1200) break; }
            return text.ToString();
        }

        private static void WriteStatus(string path, string line)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\n");
                using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                    stream.Write(bytes, 0, bytes.Length);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
