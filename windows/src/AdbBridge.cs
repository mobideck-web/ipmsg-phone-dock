using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace IPMsg.Dock
{
    public sealed class CommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public bool OutputTruncated { get; set; }

        public void EnsureSuccess(string action)
        {
            if (ExitCode != 0)
                throw new InvalidOperationException(action + ": " + UsefulError());
        }

        public string UsefulError()
        {
            string detail = String.IsNullOrWhiteSpace(Error) ? Output : Error;
            if (String.IsNullOrWhiteSpace(detail)) detail = "ADB exited with code " + ExitCode + ".";
            detail = detail.Trim();
            return detail.Length > 1200 ? detail.Substring(detail.Length - 1200) : detail;
        }
    }

    public sealed class PhoneDevice
    {
        public string Serial { get; set; }
        public string State { get; set; }
        public string Model { get; set; }
        // Set only by identity-aware resolution. The UI rechecks this value before
        // reusing approval, so a reused TCP address cannot silently change phones.
        public string VerifiedIdentity { get; internal set; }
        public override string ToString()
        {
            string name = String.IsNullOrWhiteSpace(Model) ? Serial : Model.Replace('_', ' ');
            return name + (State == "device" ? "" : " — " + State) + "  (" + Serial + ")";
        }
    }

    public sealed class MdnsService
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Endpoint { get; set; }
    }

    public sealed class AdbBridge
    {
        public const int ServerPort = 5041;
        private const int OutputLimit = 262144;
        private readonly SemaphoreSlim keyGate = new SemaphoreSlim(1, 1);
        private bool keysReady;
        public string AdbPath { get; private set; }
        public string StateDirectory { get; private set; }
        private string KeyPath { get { return Path.Combine(StateDirectory, "keys", "adbkey"); } }

        public AdbBridge(string runtimeDirectory, string stateDirectory)
        {
            if (String.IsNullOrWhiteSpace(runtimeDirectory)) throw new ArgumentException("Runtime directory is required.");
            if (String.IsNullOrWhiteSpace(stateDirectory)) throw new ArgumentException("Private state directory is required.");
            AdbPath = Path.Combine(Path.GetFullPath(runtimeDirectory), "adb.exe");
            StateDirectory = Path.GetFullPath(stateDirectory);
            Directory.CreateDirectory(Path.Combine(StateDirectory, "keys"));
            Directory.CreateDirectory(Path.Combine(StateDirectory, "android"));
        }

        // The dock owns a separate ADB server and keys. Never kill the user's normal ADB server.
        public void ConfigureEnvironment(ProcessStartInfo start)
        {
            start.EnvironmentVariables["ADB"] = AdbPath;
            start.EnvironmentVariables["ADB_SERVER_PORT"] = ServerPort.ToString();
            start.EnvironmentVariables["ANDROID_ADB_SERVER_PORT"] = ServerPort.ToString();
            start.EnvironmentVariables["ADB_SERVER_SOCKET"] = "tcp:127.0.0.1:" + ServerPort;
            start.EnvironmentVariables["ADB_VENDOR_KEYS"] = KeyPath;
            start.EnvironmentVariables["ANDROID_USER_HOME"] = Path.Combine(StateDirectory, "android");
            start.EnvironmentVariables["ANDROID_SDK_HOME"] = StateDirectory;
        }

        public async Task EnsureReadyAsync(CancellationToken token)
        {
            await keyGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (keysReady) return;
                if (!File.Exists(AdbPath)) throw new FileNotFoundException("Bundled Android tools are missing. Extract the complete IPMsg Dock package.", AdbPath);
                if (!File.Exists(KeyPath))
                {
                    CommandResult generated = await ExecuteAsync(new string[] { "keygen", KeyPath }, token, 15000).ConfigureAwait(false);
                    generated.EnsureSuccess("Could not create the dock's private device key");
                    if (!File.Exists(KeyPath)) throw new IOException("ADB did not create its private device key.");
                }
                keysReady = true;
            }
            finally { keyGate.Release(); }
        }

        public Task<CommandResult> RunAsync(string[] args, CancellationToken token, int timeoutMs = 30000)
        {
            if (args == null) throw new ArgumentNullException("args");
            if (timeoutMs < 1) throw new ArgumentOutOfRangeException("timeoutMs");
            string[] selected = (string[])args.Clone();
            // Process startup and Framework pipe setup can execute synchronously before an
            // async method yields. Keep that work away from the window/Windows drag thread.
            return Task.Run(async delegate
            {
                await EnsureReadyAsync(token).ConfigureAwait(false);
                return await ExecuteAsync(selected, token, timeoutMs).ConfigureAwait(false);
            }, token);
        }

        private async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken token, int timeoutMs, string privateInput = null)
        {
            token.ThrowIfCancellationRequested();
            if (timeoutMs < 1) throw new ArgumentOutOfRangeException("timeoutMs");
            StringBuilder command = new StringBuilder("-P " + ServerPort);
            foreach (string arg in args) command.Append(' ').Append(QuoteArgument(arg ?? ""));
            ProcessStartInfo start = new ProcessStartInfo(AdbPath, command.ToString());
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.RedirectStandardInput = privateInput != null;
            start.StandardOutputEncoding = new UTF8Encoding(false);
            start.StandardErrorEncoding = new UTF8Encoding(false);
            start.WorkingDirectory = Path.GetDirectoryName(AdbPath);
            ConfigureEnvironment(start);
            Process process = new Process();
            Task output = null;
            Task error = null;
            bool started = false;
            try
            {
                process.StartInfo = start;
                process.EnableRaisingEvents = true;
                TaskCompletionSource<bool> exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += delegate { exited.TrySetResult(true); };
                if (!process.Start()) throw new IOException("Could not start the bundled Android tools.");
                started = true;
                CapturedOutput capturedOutput = new CapturedOutput();
                CapturedOutput capturedError = new CapturedOutput();
                output = ReadBoundedAsync(process.StandardOutput, capturedOutput);
                error = ReadBoundedAsync(process.StandardError, capturedError);
                if (privateInput != null) { await process.StandardInput.WriteLineAsync(privateInput).ConfigureAwait(false); process.StandardInput.Close(); }
                if (process.HasExited) exited.TrySetResult(true);
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(timeoutMs);
                    TaskCompletionSource<bool> interrupted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (timeout.Token.Register(delegate { interrupted.TrySetResult(true); }))
                    {
                        Task winner = await Task.WhenAny(exited.Task, interrupted.Task).ConfigureAwait(false);
                        if (winner != exited.Task)
                        {
                            KillOwned(process);
                            // Give the killed client a bounded chance to release its local
                            // partial-file handle before the transfer service removes it.
                            await Task.WhenAny(exited.Task, Task.Delay(1000)).ConfigureAwait(false);
                            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                            throw new TimeoutException("The phone did not respond within " + Math.Max(1, timeoutMs / 1000) + " seconds. Check its connection and authorization.");
                        }
                        // ADB's spawned server can inherit pipe handles after this command exits.
                        // Never close a StreamReader while its Framework ReadAsync is pending:
                        // Close/Dispose can wait for the read and freeze the caller indefinitely.
                        Task drain = Task.WhenAll(output, error);
                        Task drained = await Task.WhenAny(drain, interrupted.Task, Task.Delay(250)).ConfigureAwait(false);
                        if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                        if (drained == interrupted.Task)
                            throw new TimeoutException("The phone response timed out. Check its connection and try again.");
                        return new CommandResult
                        {
                            ExitCode = process.ExitCode,
                            Output = capturedOutput.Snapshot(),
                            Error = capturedError.Snapshot(),
                            // Without EOF the snapshot may be incomplete. Structured-output
                            // consumers must reject it rather than accept half a listing.
                            OutputTruncated = capturedOutput.Truncated || drained != drain
                        };
                    }
                }
            }
            finally
            {
                if (!started) process.Dispose();
                else
                {
                    // Cleanup runs only after the readers themselves reach EOF. It never
                    // blocks a caller or waits synchronously inside a Process.Exited callback.
                    Task cleanup = DisposeAfterReadsAsync(process, output, error);
                    Observe(cleanup);
                }
            }
        }

        private static async Task ReadBoundedAsync(StreamReader reader, CapturedOutput captured)
        {
            char[] buffer = new char[4096];
            try
            {
                while (true)
                {
                    int count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (count == 0) break;
                    captured.Append(buffer, count);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        private static async Task DisposeAfterReadsAsync(Process process, Task output, Task error)
        {
            try
            {
                if (output != null) await output.ConfigureAwait(false);
                if (error != null) await error.ConfigureAwait(false);
            }
            catch (Exception) { }
            finally { process.Dispose(); }
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private sealed class CapturedOutput
        {
            private readonly object gate = new object();
            private readonly StringBuilder text = new StringBuilder();
            private bool truncated;
            public bool Truncated { get { lock (gate) return truncated; } }
            public string Snapshot() { lock (gate) return text.ToString(); }
            public void Append(char[] buffer, int count)
            {
                lock (gate)
                {
                    text.Append(buffer, 0, count);
                    if (text.Length > OutputLimit)
                    {
                        text.Remove(0, text.Length - OutputLimit);
                        truncated = true;
                    }
                }
            }
        }

        internal static void KillOwned(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        public Task<CommandResult> ShellAsync(string serial, string script, CancellationToken token, int timeoutMs = 30000)
        {
            ValidateSerial(serial);
            if (script == null || script.IndexOf('\0') >= 0) throw new ArgumentException("Invalid phone command.");
            return RunAsync(new string[] { "-s", serial, "shell", script }, token, timeoutMs);
        }

        public async Task<List<PhoneDevice>> DevicesAsync(CancellationToken token)
        {
            CommandResult result = await RunAsync(new string[] { "devices", "-l" }, token).ConfigureAwait(false);
            result.EnsureSuccess("Could not list phones");
            if (result.OutputTruncated) throw new IOException("The phone listing was incomplete. Refresh devices and try again.");
            List<PhoneDevice> devices = new List<PhoneDevice>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in result.Output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal)) continue;
                string[] parts = Regex.Split(line.Trim(), @"\s+");
                if (parts.Length < 2) continue;
                if (parts[1] != "device" && parts[1] != "offline" && parts[1] != "unauthorized" && parts[1] != "recovery" && parts[1] != "sideload" && parts[1] != "no") continue;
                try { ValidateSerial(parts[0]); } catch (ArgumentException) { continue; }
                if (!seen.Add(parts[0])) throw new IOException("The phone listing contained duplicate transports. Refresh devices and try again.");
                if (devices.Count >= 128) throw new IOException("Too many phone transports were reported. Disconnect unused phones and try again.");
                string model = "";
                foreach (string part in parts) if (part.StartsWith("model:", StringComparison.Ordinal)) model = part.Substring(6);
                devices.Add(new PhoneDevice { Serial = parts[0], State = parts[1] == "no" ? "no permissions" : parts[1], Model = model });
            }
            return devices;
        }

        public async Task<List<MdnsService>> MdnsServicesAsync(CancellationToken token, bool includePairing = false)
        {
            CommandResult result = await RunAsync(new string[] { "mdns", "services" }, token, 6000).ConfigureAwait(false);
            result.EnsureSuccess("Could not discover paired phones");
            if (result.OutputTruncated) throw new IOException("Wireless discovery returned too many devices. Use the phone's connection address.");
            List<MdnsService> services = new List<MdnsService>();
            foreach (string line in result.Output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = Regex.Split(line.Trim(), @"\s+");
                if (parts.Length < 3) continue;
                string type = parts[parts.Length - 2].TrimEnd('.');
                if (type != "_adb-tls-connect._tcp" && !(includePairing && type == "_adb-tls-pairing._tcp")) continue;
                string endpoint = parts[parts.Length - 1];
                try { ValidateEndpoint(endpoint); } catch (ArgumentException) { continue; }
                string name = parts[parts.Length - 3].TrimEnd('.');
                if (name.Length == 0 || name.Length > 256) continue;
                services.Add(new MdnsService { Name = name, Type = type, Endpoint = endpoint });
                if (services.Count >= 128) break;
            }
            return services;
        }

        public async Task<string> DisplaySizeAsync(string serial, CancellationToken token)
        {
            CommandResult result = await ShellAsync(serial, "wm size", token).ConfigureAwait(false);
            result.EnsureSuccess("Could not read the screen dimensions");
            MatchCollection sizes = Regex.Matches(result.Output, @"(?:Physical|Override) size:\s*(\d{2,5}x\d{2,5})");
            if (sizes.Count == 0) throw new IOException("The phone did not report its display dimensions.");
            return sizes[sizes.Count - 1].Groups[1].Value;
        }

        public async Task<string> ModelAsync(string serial, CancellationToken token)
        {
            CommandResult result = await ShellAsync(serial, "getprop ro.product.model", token).ConfigureAwait(false);
            result.EnsureSuccess("Could not read the phone name");
            string model = result.Output.Trim().Replace("\r", " ").Replace("\n", " ");
            return model.Length > 128 ? model.Substring(0, 128) : model;
        }

        public async Task PairAsync(string endpoint, string code, CancellationToken token)
        {
            ValidateEndpoint(endpoint);
            if (code == null || !Regex.IsMatch(code, @"^\d{6}$")) throw new ArgumentException("Enter the six-digit pairing code shown on the phone.");
            CommandResult result = await RunAsync(new string[] { "pair", endpoint, code }, token, 45000).ConfigureAwait(false);
            result.EnsureSuccess("Pairing failed");
            if (result.Output.IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Pairing failed: " + result.UsefulError());
        }

        public Task PairQrAsync(string endpoint, string secret, CancellationToken token)
        {
            ValidateEndpoint(endpoint);
            if (secret == null || !Regex.IsMatch(secret, "^[0-9a-f]{32}$")) throw new ArgumentException("Invalid temporary QR pairing secret.");
            return Task.Run(async delegate {
                await EnsureReadyAsync(token).ConfigureAwait(false);
                CommandResult result = await ExecuteAsync(new[] { "pair", endpoint }, token, 45000, secret).ConfigureAwait(false);
                if (result.ExitCode != 0 || result.OutputTruncated || result.Output.IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new IOException("QR pairing was refused or expired. Generate a new QR code and scan it again.");
            }, token);
        }

        public async Task ConnectAsync(string endpoint, CancellationToken token)
        {
            ValidateEndpoint(endpoint);
            CommandResult result = await RunAsync(new string[] { "connect", endpoint }, token, 20000).ConfigureAwait(false);
            result.EnsureSuccess("Connection failed");
            if (result.Output.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Connection failed: " + result.UsefulError());
        }

        public async Task KeyAsync(string serial, int key, CancellationToken token)
        {
            if (key < 0 || key > 400) throw new ArgumentOutOfRangeException("key");
            CommandResult result = await ShellAsync(serial, "input keyevent " + key, token, 10000).ConfigureAwait(false);
            result.EnsureSuccess("Could not send the navigation key");
        }

        public async Task DisconnectAsync(string serial, CancellationToken token)
        {
            ValidateSerial(serial);
            if (serial.IndexOf(':') < 0) return; // USB authorization belongs to the phone; do not revoke it silently.
            CommandResult result = await RunAsync(new string[] { "disconnect", serial }, token, 10000).ConfigureAwait(false);
            result.EnsureSuccess("Could not disconnect the phone");
        }

        public static void ValidateSerial(string serial)
        {
            if (String.IsNullOrWhiteSpace(serial) || serial.Length > 256 || serial.StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException("Select a phone first.");
            foreach (char c in serial) if (Char.IsControl(c) || Char.IsWhiteSpace(c)) throw new ArgumentException("Invalid phone identifier.");
        }

        public static void ValidateEndpoint(string endpoint)
        {
            if (endpoint == null || !Regex.IsMatch(endpoint, @"^\d{1,3}(?:\.\d{1,3}){3}:\d{1,5}$"))
                throw new ArgumentException("Enter the phone's local IPv4 address and port, for example 192.168.1.20:37123.");
            int colon = endpoint.LastIndexOf(':');
            string address = endpoint.Substring(0, colon);
            IPAddress ip;
            int port;
            if (!IPAddress.TryParse(address, out ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
                !Int32.TryParse(endpoint.Substring(colon + 1), out port) || port < 1 || port > 65535)
                throw new ArgumentException("Invalid local address or port.");
            byte[] bytes = ip.GetAddressBytes();
            bool local = bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
            if (!local || ip.ToString() != address) throw new ArgumentException("Use the phone's private local-network IPv4 address.");
            if (port == 5555) throw new ArgumentException("Use Android 11+ Wireless debugging with its pairing and connection ports. Legacy unencrypted port 5555 is not supported; use USB on older phones.");
        }

        // CommandLineToArgvW-compatible quoting; no shell ever interprets these arguments.
        public static string QuoteArgument(string value)
        {
            if (value == null) value = "";
            if (value.IndexOf('\0') >= 0) throw new ArgumentException("Arguments cannot contain a NUL character.");
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }

        public static string ShellQuote(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0 || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
                throw new ArgumentException("Invalid phone path.");
            return "'" + value.Replace("'", "'\"'\"'") + "'";
        }
    }
}
