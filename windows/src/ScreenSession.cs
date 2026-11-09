using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace IPMsg.Dock
{
    public sealed class ScreenSession : IDisposable
    {
        private readonly AdbBridge bridge;
        private readonly string scrcpyPath;
        private readonly object gate = new object();
        private readonly Queue<string> recent = new Queue<string>();
        private Process process;
        private bool disposed;
        public event Action<string> LogLine;
        public event Action Exited;
        public string ProxyStatusFile { get; private set; }

        public ScreenSession(AdbBridge bridge, string runtimeDirectory)
        {
            if (bridge == null) throw new ArgumentNullException("bridge");
            this.bridge = bridge;
            scrcpyPath = Path.Combine(Path.GetFullPath(runtimeDirectory), "scrcpy.exe");
        }

        public bool IsRunning
        {
            get
            {
                lock (gate)
                {
                    if (process == null) return false;
                    try { return !process.HasExited; }
                    catch (InvalidOperationException) { return false; }
                }
            }
        }

        public string RecentLog
        {
            get { lock (gate) return String.Join(Environment.NewLine, recent.ToArray()); }
        }

        public Process Start(string serial, string title, bool viewOnly, bool sound, string qualityProfile, bool screenOff = false)
        {
            AdbBridge.ValidateSerial(serial);
            if (String.IsNullOrWhiteSpace(title) || title.Length > 200 || title.IndexOf('\0') >= 0) throw new ArgumentException("A unique window title is required.");
            if (!File.Exists(scrcpyPath)) throw new FileNotFoundException("Bundled screen-sharing runtime is missing. Extract the complete IPMsg Dock package.", scrcpyPath);
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("ScreenSession");
                if (process != null)
                {
                    if (!process.HasExited) throw new InvalidOperationException("A phone screen session is already running.");
                    CancelSession(ProxyStatusFile);
                    process.Dispose();
                    process = null;
                }
                string size = "1920", bitrate = "12M", codec = "h264", videoBuffer = "0", maxFps = "60";
                if (String.Equals(qualityProfile, "smooth", StringComparison.OrdinalIgnoreCase) || String.Equals(qualityProfile, "economy", StringComparison.OrdinalIgnoreCase)) { size = "1280"; bitrate = "6M"; }
                else if (String.Equals(qualityProfile, "sharp", StringComparison.OrdinalIgnoreCase)) { size = "2560"; bitrate = "24M"; }
                else if (String.Equals(qualityProfile, "native", StringComparison.OrdinalIgnoreCase)) { size = "0"; bitrate = "32M"; }
                else if (String.Equals(qualityProfile, "cinema", StringComparison.OrdinalIgnoreCase)) { size = "2560"; bitrate = "20M"; codec = "h265"; videoBuffer = "50"; }
                else if (qualityProfile == "gaming90") { size = "1600"; bitrate = "12M"; maxFps = "90"; }
                else if (qualityProfile == "gaming120") { size = "1600"; bitrate = "16M"; maxFps = "120"; }
                List<string> args = new List<string>();
                args.Add("--serial=" + serial);
                args.Add("--window-title=" + title);
                args.Add("--window-borderless");
                args.Add("--no-window-aspect-ratio-lock");
                args.Add("--max-size=" + size);
                args.Add("--max-fps=" + maxFps);
                args.Add("--video-bit-rate=" + bitrate);
                args.Add("--video-codec=" + codec);
                args.Add("--video-buffer=" + videoBuffer);
                args.Add("--hwdec=auto");
                args.Add("--no-clipboard-autosync");
                args.Add("--no-power-on");
                if (viewOnly) args.Add("--no-control");
                if (screenOff && !viewOnly) { args.Add("--turn-screen-off"); args.Add("--stay-awake"); }
                if (!sound) args.Add("--no-audio");
                else { args.Add("--audio-source=output"); args.Add(videoBuffer != "0" ? "--audio-buffer=60" : "--audio-buffer=40"); }
                StringBuilder command = new StringBuilder();
                foreach (string arg in args) command.Append(AdbBridge.QuoteArgument(arg)).Append(' ');
                ProcessStartInfo start = new ProcessStartInfo(scrcpyPath, command.ToString());
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.StandardOutputEncoding = new UTF8Encoding(false);
                start.StandardErrorEncoding = new UTF8Encoding(false);
                start.WorkingDirectory = Path.GetDirectoryName(scrcpyPath);
                bridge.ConfigureEnvironment(start);
                string sessions = Path.Combine(bridge.StateDirectory, "sessions");
                Directory.CreateDirectory(sessions);
                ProxyStatusFile = Path.Combine(sessions, Guid.NewGuid().ToString("N") + ".status");
                start.EnvironmentVariables["ADB"] = System.Reflection.Assembly.GetEntryAssembly().Location;
                start.EnvironmentVariables["IPMSG_DOCK_ADB_PROXY"] = "1";
                start.EnvironmentVariables["IPMSG_DOCK_PROXY_RUNTIME"] = Path.GetDirectoryName(scrcpyPath);
                start.EnvironmentVariables["IPMSG_DOCK_PROXY_STATE"] = bridge.StateDirectory;
                start.EnvironmentVariables["IPMSG_DOCK_PROXY_SERIAL"] = serial;
                start.EnvironmentVariables["IPMSG_DOCK_PROXY_STATUS"] = ProxyStatusFile;
                start.EnvironmentVariables["ANDROID_SERIAL"] = serial;
                // Use the bundled server even when another scrcpy is installed on Windows.
                start.EnvironmentVariables["SCRCPY_SERVER_PATH"] = Path.Combine(start.WorkingDirectory, "scrcpy-server");
                Process child = new Process();
                child.StartInfo = start;
                child.EnableRaisingEvents = true;
                child.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { OnLog(e.Data); };
                child.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { OnLog(e.Data); };
                child.Exited += delegate { OnExited(child); };
                recent.Clear();
                process = child;
                try
                {
                    if (!child.Start()) throw new IOException("Could not start the phone display.");
                    child.BeginOutputReadLine();
                    child.BeginErrorReadLine();
                    return child;
                }
                catch
                {
                    AdbBridge.KillOwned(child);
                    process = null;
                    child.Dispose();
                    throw;
                }
            }
        }

        private void OnLog(string line)
        {
            if (String.IsNullOrEmpty(line)) return;
            if (line.Length > 2048) line = line.Substring(0, 2048);
            Action<string> handler;
            lock (gate)
            {
                if (disposed) return;
                recent.Enqueue(line);
                while (recent.Count > 100) recent.Dequeue();
                handler = LogLine;
            }
            if (handler != null) { try { handler(line); } catch { } }
        }

        private void OnExited(Process child)
        {
            Action handler;
            lock (gate)
            {
                if (disposed || process != child) return;
                CancelSession(ProxyStatusFile);
                handler = Exited;
            }
            if (handler != null) { try { handler(); } catch { } }
        }

        public void Stop()
        {
            Process child;
            string status;
            lock (gate) { child = process; process = null; status = ProxyStatusFile; }
            CancelSession(status);
            if (child == null) return;
            // Never taskkill by image name, kill ADB's shared server, or touch another scrcpy instance.
            AdbBridge.KillOwned(child);
            try { child.CancelOutputRead(); } catch (InvalidOperationException) { }
            try { child.CancelErrorRead(); } catch (InvalidOperationException) { }
            child.Dispose();
        }

        private static void CancelSession(string status)
        {
            if (String.IsNullOrEmpty(status)) return;
            try { File.WriteAllText(status + ".cancel", "cancel"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            Stop();
        }
    }
}
