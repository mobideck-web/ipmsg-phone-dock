using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IPMsg.Dock
{
    internal sealed partial class DockForm : Form
    {
        internal static readonly Color Background = GlassTheme.Background;
        internal static readonly Color Card = GlassTheme.Card;
        internal static readonly Color Accent = GlassTheme.Accent;
        private readonly AdbBridge bridge;
        private readonly FileTransferService transfers;
        private readonly ScreenSession session;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource operation;
        private readonly TableLayoutPanel layout = new TableLayoutPanel();
        private readonly Panel preview = new Panel();
        private readonly PhoneWelcome placeholder = new PhoneWelcome();
        private readonly ComboBox phones = new ComboBox();
        private readonly ComboBox mode = new ComboBox();
        private readonly ComboBox quality = new ComboBox();
        private readonly CheckBox audio = new CheckBox();
        private readonly SharedFileList files = new SharedFileList();
        private readonly Label pathLabel = new Label();
        private readonly Label status = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Button connect, wireless, cancel, back, home, trayToggle, captureButton, hideFiles, clearFiles;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly string stateDirectory;
        private string serial, windowTitle, proxyStatus = "";
        private Process child;
        private IntPtr screenWindow;
        private bool busy, proxyBusy, connecting, collapsed, trayOpen, stopping, closingRequested, finalClose;
        private Form edgeTab;
        private Rectangle expandedBounds;
        private DateTime startupTime, lastProgress = DateTime.MinValue;
        private double phoneAspect = 9.0 / 20.0;

        public DockForm(string runtime, string state, bool startInBackground = false)
        {
            stateDirectory = state;
            bridge = new AdbBridge(runtime, state);
            transfers = new FileTransferService(bridge, Path.Combine(state, "staging"));
            session = new ScreenSession(bridge, runtime);
            session.Exited += delegate { Ui(SessionEnded); };
            session.LogLine += delegate(string line) { if (line.IndexOf("ERROR:", StringComparison.OrdinalIgnoreCase) >= 0) Ui(delegate { SetStatus(line, true); }); };
            Text = "IPMsg · Phone Dock 0.7"; FormBorderStyle = FormBorderStyle.None;
            Font = new Font("Segoe UI", 9.5f); BackColor = Background; ForeColor = GlassTheme.Text;
            AutoScaleMode = AutoScaleMode.Dpi; MinimumSize = new Size(280, 460);
            StartPosition = FormStartPosition.Manual;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Bounds = new Rectangle(area.Right - 288, area.Top + 8, 280, Math.Min(960, area.Height - 16));
            try { Icon = new Icon(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "ipmsg.ico")); } catch (IOException) { }

            layout.Dock = DockStyle.Fill; layout.ColumnCount = 1; layout.RowCount = 6;
            layout.Padding = new Padding(6, 3, 6, 5);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 42, 32, 190, 36 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Controls.Add(layout);

            var header = Row();
            var brand = new Label { Text = "IPMsg", Width = 100, Height = 40, AutoEllipsis = true,
                ForeColor = GlassTheme.Text, Font = new Font("Segoe UI Semibold", 11), Padding = new Padding(8, 9, 0, 0) };
            fullscreenButton = IconButton("layout", "Window layout options", delegate { ShowWindowActions(true); });
            fullscreenButton.MouseEnter += delegate { ShowWindowActions(false); };
            moreButton = IconButton("more", "Connection and stream options", delegate { ShowMore(); });
            var close = IconButton("close", "Close dock", delegate { Close(); });
            header.Controls.AddRange(new Control[] { brand, fullscreenButton, moreButton, close });
            header.Layout += delegate {
                int width=Math.Max(60,header.ClientSize.Width-header.Controls.Cast<Control>().Where(item=>item!=brand && item.Visible).Sum(item=>item.Width+item.Margin.Horizontal)-12);
                if(brand.Width!=width) brand.Width=width;
            };
            header.MouseDown += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left && !fullscreen) BeginDockMove(); };
            brand.MouseDown += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left && !fullscreen) BeginDockMove(); };
            layout.Controls.Add(header, 0, 0);

            // Choice controls are state holders for the concise More menus, not extra page rows.
            StyleCombo(phones);
            StyleCombo(mode); mode.Items.AddRange(new object[] { "Control phone", "View only" }); mode.SelectedIndex = 0;
            StyleCombo(quality); quality.Items.AddRange(new object[] { "Balanced", "Smooth", "Sharp", "Native", "Cinema", "Gaming · 90 fps", "Gaming · 120 fps" }); quality.SelectedIndex = 0;
            audio.Text = "Sound"; audio.Checked = false;
            connect = Button("Connect", async delegate { if (session.IsRunning) await Disconnect(); else await Connect(); });
            connect.AutoSize = false; connect.Size = new Size(172, 44); ((GlassButton)connect).Primary = true;
            wireless = Button("Set up Wi-Fi", async delegate { await OpenWireless(); });
            wireless.AutoSize = false; wireless.Size = new Size(172, 34);

            preview.Dock = DockStyle.Fill; preview.BackColor = Color.Black; preview.Margin = new Padding(0, 3, 0, 3);
            placeholder.Dock = DockStyle.Fill; placeholder.TextAlign = ContentAlignment.MiddleCenter;
            placeholder.BackColor = Color.Transparent; placeholder.ForeColor = GlassTheme.MutedText;
            placeholder.Font = new Font("Segoe UI", 18); placeholder.Padding = new Padding(0, 0, 0, 115);
            placeholder.Text = "Your phone.\nRight here.";
            preview.Controls.Add(placeholder); preview.Controls.Add(connect); preview.Controls.Add(wireless);
            connect.BringToFront(); wireless.BringToFront();
            preview.Resize += delegate { FitScreen(); PositionEntryActions(); };
            layout.Controls.Add(CreatePhoneSurface(), 0, 1);

            var navigation = Row();
            back = IconButton("back", "Back", async delegate { await Navigation(4); });
            home = IconButton("home", "Home", async delegate { await Navigation(3); });
            trayToggle = Button("Files & photos", async delegate { await ToggleFilesShelfAsync(); });
            trayToggle.AccessibleName = "Open phone files, photos and folders";
            captureButton = IconButton("camera", "Copy phone screenshot", async delegate { await CapturePhoneAsync(); });
            captureButton.AccessibleName = "Copy phone screenshot";
            navigation.Controls.AddRange(new Control[] { back, home, trayToggle, captureButton });
            layout.Controls.Add(navigation, 0, 2);

            pathLabel.Dock = DockStyle.Fill; pathLabel.ForeColor = GlassTheme.MutedText; pathLabel.AutoEllipsis = true;
            pathLabel.Text = "From your phone"; pathLabel.Padding = new Padding(5, 6, 0, 0);
            var shelfHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, BackColor = Color.Transparent };
            shelfHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); shelfHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30)); shelfHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
            clearFiles = IconButton("clear", "Clear temporary files and end sharing", delegate { ClearTemporaryShelf(); }); clearFiles.Size = new Size(26, 26); clearFiles.Margin = Padding.Empty;
            hideFiles = IconButton("close", "Hide files and photos", delegate { SetShelfVisible(false); }); hideFiles.Size = new Size(26, 26); hideFiles.Margin = Padding.Empty;
            shelfHeader.Controls.Add(pathLabel, 0, 0); shelfHeader.Controls.Add(clearFiles, 1, 0); shelfHeader.Controls.Add(hideFiles, 2, 0); layout.Controls.Add(shelfHeader, 0, 3);
            files.Dock = DockStyle.Fill; files.HideSelection = false; files.MultiSelect = true;
            files.BackColor = Card; files.ForeColor = ForeColor; files.BorderStyle = BorderStyle.None;
            layout.Controls.Add(files, 0, 4);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
                Margin = Padding.Empty, Padding = new Padding(0, 3, 0, 0), BackColor = Color.Transparent };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 67));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 3));
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.Text = "Ready to connect";
            progress.Dock = DockStyle.Fill; progress.Maximum = 1000;
            cancel = Button("Cancel", delegate { CancelOperation(); }); cancel.Visible = false;
            footer.Controls.Add(status, 0, 0); footer.Controls.Add(cancel, 1, 0); footer.Controls.Add(progress, 0, 1);
            footer.SetColumnSpan(progress, 2); layout.Controls.Add(footer, 0, 5);

            EnableDrop(this); EnableDrop(files);
            timer.Interval = 100; timer.Tick += delegate { TickSession(); }; timer.Start();
            Shown += async delegate { InitializeDesktop(); NativeGlass.Apply(this); if (startInBackground) { await HideToBackgroundAsync(); return; } await RefreshDevices(); if (!IsDisposed && !session.IsRunning) connect.Focus(); };
            ResizeEnd += delegate { FitScreen(); UpdateDesktopReservation(); };
            FormClosing += OnClosing; SystemEvents.SessionSwitch += SessionSwitch;
            InitializeFeatures(); InitializeBridgeShelf(); InitializePresentation(); InitializeCacheMaintenance(); UpdateControls();
        }

        internal static Button Button(string text, EventHandler clicked)
        {
            var button = new GlassButton { Text = text, AutoSize = true, Height = 30, MinimumSize = new Size(30, 28),
                FlatStyle = FlatStyle.Flat, BackColor = Color.Transparent, ForeColor = GlassTheme.Text,
                Cursor = Cursors.Hand, Margin = new Padding(2), Padding = new Padding(5, 0, 5, 0), TabStop = true };
            button.FlatAppearance.BorderSize = 0; button.Click += clicked; return button;
        }
        private FlowLayoutPanel Row() { return new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty }; }
        internal static void StyleCombo(ComboBox combo) { combo.DropDownStyle = ComboBoxStyle.DropDownList; combo.FlatStyle = FlatStyle.Flat; combo.BackColor = Card; combo.ForeColor = GlassTheme.Text; }
        private void Ui(Action action) { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(action); } catch (InvalidOperationException) { } }
        private void SetStatus(string text, bool error = false) { if (IsDisposed || Disposing) return; status.Text = text; status.ForeColor = error ? GlassTheme.Danger : GlassTheme.MutedText; }
        private string SelectedSerial { get { var item = phones.SelectedItem as PhoneDevice; return item == null ? null : item.Serial; } }
        private bool CanTransfer() { if (backgroundHidden || backgroundTransition || closingRequested) return false; if (String.IsNullOrEmpty(serial) || !session.IsRunning) { SetStatus("Connect a phone first."); return false; } return !busy && !connecting && !proxyBusy && !preparing; }
        private void UpdateControls()
        {
            if (IsDisposed || Disposing) return;
            bool active = session.IsRunning, copying = busy || proxyBusy || preparing;
            connect.Enabled = !copying && !connecting; connect.Text = connecting ? "Connecting…" : "Connect";
            phones.Enabled = wireless.Enabled = !active && !copying && !connecting;
            mode.Enabled = quality.Enabled = audio.Enabled = !active && !copying && !connecting;
            back.Enabled = home.Enabled = active && mode.SelectedIndex == 0 && !copying && !connecting;
            trayToggle.Enabled = captureButton.Enabled = active && !copying && !connecting;
            cancel.Visible = copying || connecting;
            files.Enabled = active && !copying && !connecting;
            if (files.ContextMenuStrip != null) files.ContextMenuStrip.Enabled = files.Enabled;
            fullscreenButton.Enabled = true; UpdateQuickControls();
            ApplyChrome();
        }

        private async Task RefreshDevices()
        {
            if (backgroundHidden || backgroundTransition || closingRequested || busy || connecting || session.IsRunning) return;
            connecting = true; UpdateControls();
            try
            {
                string previous = SelectedSerial ?? connections.PreferredSerial;
                var list = await bridge.DevicesAsync(lifetime.Token);
                if (backgroundHidden || backgroundTransition || closingRequested) return;
                phones.Items.Clear(); foreach (var phone in list) phones.Items.Add(phone);
                for (int i = 0; i < phones.Items.Count; i++) if (((PhoneDevice)phones.Items[i]).Serial == previous) phones.SelectedIndex = i;
                // Keep the remembered identity when its old transport disappears.
                // ResolveAsync can find this phone on its new USB/Wi-Fi transport;
                // choosing the first row here could silently select another phone.
                if (phones.SelectedIndex < 0 && String.IsNullOrEmpty(previous) && phones.Items.Count == 1) phones.SelectedIndex = 0;
                int ready = list.Count(phone => phone.State == "device");
                SetStatus(ready > 0 ? "Phone available" : list.Any(phone => phone.State == "unauthorized") ? "Unlock the phone and approve this PC." : "Reconnect your phone, then click Connect.");
            }
            catch (Exception e) { if (!lifetime.IsCancellationRequested) SetStatus(e.Message, true); }
            finally { connecting = false; UpdateControls(); }
        }

        private async Task Connect()
        {
            if (backgroundHidden || backgroundTransition || closingRequested || busy || proxyBusy || connecting || preparing || session.IsRunning) return;
            connecting = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); UpdateControls();
            try
            {
                SetStatus("Finding your phone…");
                PhoneDevice device = await connections.ResolveAsync(SelectedSerial, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                currentIdentity = await connections.IdentityAsync(device.Serial, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (String.IsNullOrEmpty(device.VerifiedIdentity) || currentIdentity != device.VerifiedIdentity)
                    throw new IOException("The phone connection changed while opening. Choose the phone again before connecting.");
                if (backgroundHidden || backgroundTransition || closingRequested) return;
                if (!connections.IsTrusted(currentIdentity))
                {
                    if (MessageBox.Show(this, "Authorize this PC for phone viewing, control and file transfer?\n\nAndroid debugging grants broad device access. Use your own trusted PC. The selected mode controls this session.\n\nThis approval is remembered for one-click reconnect. Revoke Android authorization on your phone when no longer needed.", "Connect " + (device.Model ?? device.Serial).Replace('_', ' '), MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
                    operation.Token.ThrowIfCancellationRequested();
                    if (backgroundHidden || backgroundTransition || closingRequested) return;
                    connections.Trust(currentIdentity, device);
                }
                connections.Remember(device, currentIdentity);
                phones.Items.Clear(); phones.Items.Add(device); phones.SelectedIndex = 0;
                serial = device.Serial; SetStatus("Opening your phone…");
                string size = await bridge.DisplaySizeAsync(serial, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (backgroundHidden || backgroundTransition || closingRequested) return;
                var dimensions = size.Split('x'); phoneAspect = Double.Parse(dimensions[0]) / Double.Parse(dimensions[1]);
                windowTitle = "IPMsgDock-" + Guid.NewGuid().ToString("N");
                child = session.Start(serial, windowTitle, mode.SelectedIndex == 1, audio.Checked, QualityProfile(), screenOff && mode.SelectedIndex == 0);
                startupTime = DateTime.UtcNow; proxyStatus = ""; screenWindow = IntPtr.Zero;
                placeholder.Visible = true; placeholder.Text = "Opening your phone…\n\nAndroid's lock screen remains protected.";
            }
            catch (Exception e) { session.Stop(); SessionEnded(); SetStatus(e is OperationCanceledException ? "Connection cancelled." : e.Message, !(e is OperationCanceledException)); }
            finally { connecting = false; if (operation != null) { operation.Dispose(); operation = null; } UpdateControls(); }
        }

        private async Task Disconnect()
        {
            if (busy || proxyBusy || preparing) return;
            string old = serial; stopping = true; session.Stop(); SessionEnded(); stopping = false;
            if (!String.IsNullOrEmpty(old)) { try { await bridge.DisconnectAsync(old, lifetime.Token); } catch (Exception e) { SetStatus(e.Message, true); } }
        }
        private void SessionEnded()
        {
            if (session.IsRunning) return;
            if (operation != null) operation.Cancel();
            StopBridge();
            screenWindow = IntPtr.Zero; serial = null; child = null; proxyBusy = false;
            files.Items.Clear(); trayOpen = false; placeholder.Visible = true; placeholder.Text = "Your phone.\nRight here.";
            if (fullscreen) SetFullscreen(false);
            if (!stopping && !closingRequested) SetStatus("Phone disconnected. Reconnect when ready.");
            UpdateControls(); UpdateDesktopReservation();
        }

        private void TickSession()
        {
            QueueCachePrune();
            UpdateFullscreenControls();
            if (!session.IsRunning) return;
            if (screenWindow == IntPtr.Zero && child != null)
            {
                screenWindow = Native.FindOwnedWindow(child, windowTitle);
                if (screenWindow != IntPtr.Zero)
                {
                    try
                    {
                        Native.Embed(screenWindow, preview.Handle); placeholder.Visible = false; FitScreen();
                        SetStatus(mode.SelectedIndex == 0 ? "Connected" : "Connected · view only"); UpdateControls();
                    }
                    catch (Exception e) { session.Stop(); SessionEnded(); SetStatus("Could not embed the phone screen: " + e.Message, true); }
                }
                else if ((DateTime.UtcNow - startupTime).TotalSeconds > 25)
                { string detail = session.RecentLog; session.Stop(); SessionEnded(); SetStatus("Could not open the screen. " + LastLine(detail), true); }
            }
            else if (screenWindow != IntPtr.Zero && !Native.IsWindow(screenWindow)) { session.Stop(); SessionEnded(); }
            else if (screenWindow != IntPtr.Zero) FitScreen();
            ReadProxyProgress(); TickBridge();
        }
        private static string LastLine(string text) { var parts = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries); return parts.Length == 0 ? "Check debugging permission on the phone." : parts[parts.Length - 1]; }
        private void FitScreen()
        {
            if (screenWindow == IntPtr.Zero || !Native.IsWindow(screenWindow)) return;
            int w = preview.ClientSize.Width, h = preview.ClientSize.Height;
            if (w > 0 && h > 0 && !Native.HasSize(screenWindow, w, h)) Native.MoveWindow(screenWindow, 0, 0, w, h, true);
        }
        private async Task Navigation(int key)
        { if (serial == null || mode.SelectedIndex != 0 || busy || connecting) return; try { await bridge.KeyAsync(serial, key, lifetime.Token); Native.SetFocus(screenWindow); } catch (Exception e) { SetStatus(e.Message, true); } }

        private async Task Upload(string[] paths)
        {
            if (!CanTransfer()) return;
            if (paths == null || paths.Length == 0 || paths.Length > 30 || paths.Any(String.IsNullOrWhiteSpace)) { SetStatus("Choose up to 30 individual files. Folder copies are not included yet.", true); return; }
            string targetSerial = serial;
            busy = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            CancellationToken token = operation.Token; UpdateControls();
            try
            {
                IProgress<TransferProgress> reporter = TransferReporter(token);
                Task work = Task.Run(async delegate
                {
                    foreach (string path in paths) { token.ThrowIfCancellationRequested(); if (!File.Exists(path)) throw new IOException("A selected file is unavailable. Download it locally and retry."); }
                    token.ThrowIfCancellationRequested();
                    await transfers.EnsureInboxAsync(targetSerial, token).ConfigureAwait(false);
                    foreach (string path in paths) { token.ThrowIfCancellationRequested(); await transfers.UploadAsync(targetSerial, path, FileTransferService.InboxPath, reporter, token).ConfigureAwait(false); }
                }, token);
                await AwaitCancelableFileWorkAsync(work, token);
                SetStatus(paths.Length + " files copied to Downloads/IPMsg.");
            }
            catch (Exception e) { SetStatus(e is OperationCanceledException ? "Copy cancelled." : e.Message, !(e is OperationCanceledException)); }
            finally { EndOperation(); }
        }
        private static async Task AwaitCancelableFileWorkAsync(Task work, CancellationToken token)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(delegate { cancelled.TrySetResult(true); }))
            {
                if (await Task.WhenAny(work, cancelled.Task) != work) { ObserveBridgeTask(work); token.ThrowIfCancellationRequested(); }
                await work; token.ThrowIfCancellationRequested();
            }
        }
        private IProgress<TransferProgress> TransferReporter(CancellationToken token)
        {
            return new Progress<TransferProgress>(p =>
            {
                if (token.IsCancellationRequested || IsDisposed || Disposing) return;
                if ((DateTime.UtcNow - lastProgress).TotalMilliseconds < 180 && p.Bytes != p.Total) return;
                lastProgress = DateTime.UtcNow;
                progress.Value = p.Total <= 0 ? 0 : Math.Max(0, Math.Min(1000, (int)(p.Bytes * 1000.0 / p.Total)));
                string speed = p.BytesPerSecond > 0 ? " · " + Bytes((long)p.BytesPerSecond) + "/s" : "";
                string eta = p.BytesPerSecond > 0 && p.Total > p.Bytes ? " · " + Math.Ceiling((p.Total - p.Bytes) / p.BytesPerSecond) + "s left" : "";
                SetStatus(p.Name + " · " + p.Message + " " + (p.Total > 0 ? Math.Min(100, (int)(p.Bytes * 100.0 / p.Total)) + "%" : "") + speed + eta);
            });
        }
        private void EndOperation()
        {
            busy = false; if (operation != null) { operation.Dispose(); operation = null; }
            if (!IsDisposed && !Disposing) { progress.Value = 0; UpdateControls(); }
        }
        private void CancelOperation()
        {
            if (operation != null) operation.Cancel();
            if (proxyBusy && !String.IsNullOrEmpty(session.ProxyStatusFile))
            { try { File.WriteAllText(session.ProxyStatusFile + ".copycancel", Guid.NewGuid().ToString("N")); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        private void EnableDrop(Control control)
        {
            control.AllowDrop = true;
            control.DragEnter += delegate(object s, DragEventArgs e)
            { e.Effect = !busy && !proxyBusy && !preparing && !connecting && session.IsRunning && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
            control.DragDrop += async delegate(object s, DragEventArgs e)
            { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { if (collapsed) ToggleCollapse(); await Upload((string[])e.Data.GetData(DataFormats.FileDrop)); } };
        }
        private void ReadProxyProgress()
        {
            string file = session.ProxyStatusFile; if (String.IsNullOrEmpty(file)) return;
            bool copying = false;
            try { using (var held = new FileStream(file + ".copylock", FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { } }
            catch (IOException e) { int code = e.HResult & 65535; copying = code == 32 || code == 33; }
            catch (UnauthorizedAccessException) { }
            if (proxyBusy != copying) { proxyBusy = copying; UpdateControls(); if (!copying) progress.Value = 0; }
            try
            {
                string line; using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var reader = new StreamReader(stream)) line = reader.ReadToEnd();
                if (line == proxyStatus || String.IsNullOrWhiteSpace(line)) return; proxyStatus = line;
                string[] fields = line.Trim().Split('\t');
                if (fields[0] == "COPY_ERROR") SetStatus(String.Join(" ", fields.Skip(1)), true);
                else if (fields[0] == "COPY_OK") { SetStatus("Copied to phone: " + Path.GetFileName(fields.Last())); progress.Value = 0; }
                else if (fields[0] == "COPY_PROGRESS" && fields.Length >= 4)
                {
                    long done, total; Int64.TryParse(fields[2], out done); Int64.TryParse(fields[3], out total);
                    progress.Value = total > 0 ? (int)Math.Max(0, Math.Min(1000, done * 1000.0 / total)) : 0;
                    SetStatus(copying ? fields[1] + " · " + (total > 0 ? (int)(done * 100.0 / total) + "%" : "Copying…") : "Copy stopped. Retry if the file was not completed.");
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private static void CopyUnique(string source, string destination, CancellationToken token)
        {
            string name = Path.GetFileName(source), stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
            string partial = Path.Combine(destination, ".ipmsg-part-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { byte[] buffer = new byte[262144]; int count; while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); } output.Flush(true); if (output.Length != input.Length) throw new IOException("The PC copy is incomplete."); }
                for (int i = 0; i < 10000; i++)
                { token.ThrowIfCancellationRequested(); string target = Path.Combine(destination, i == 0 ? name : stem + " (" + i + ")" + extension); try { File.Move(partial, target); return; } catch (IOException) { if (!File.Exists(target)) throw; } }
                throw new IOException("Too many duplicate filenames in this PC folder.");
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        private void DockRight()
        { if (fullscreen) SetFullscreen(false); dockedRight = true; UpdateDesktopReservation(); SaveWindowPreferences(); }
        private void ToggleCollapse()
        {
            if (!collapsed && (busy || connecting || preparing)) return;
            if (fullscreen) SetFullscreen(false);
            if (!collapsed)
            {
                expandedBounds = Bounds; collapsed = true; UpdateDesktopReservation();
                Rectangle area = Screen.FromControl(this).WorkingArea;
                edgeTab = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
                    StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(area.Right - 44, Math.Max(area.Top, Math.Min(Top + 100, area.Bottom - 120)), 44, 112), BackColor = Card };
                var handle = new Button { Dock = DockStyle.Fill, Text = "IP\nMsg\n‹", BackColor = Card, ForeColor = Accent, FlatStyle = FlatStyle.Flat, AccessibleName = "Expand IPMsg Phone Dock" };
                handle.FlatAppearance.BorderSize = 0; handle.Click += delegate { ToggleCollapse(); }; EnableDrop(handle);
                var edgeMenu = new ContextMenuStrip(); edgeMenu.Items.Add("Expand dock", null, delegate { ToggleCollapse(); }); edgeMenu.Items.Add("Cancel copy", null, delegate { CancelOperation(); });
                handle.ContextMenuStrip = edgeMenu; edgeTab.Controls.Add(handle); edgeTab.Show(); Hide();
            }
            else
            {
                collapsed = false; Show(); Bounds = expandedBounds;
                if (edgeTab != null) { edgeTab.Close(); edgeTab.Dispose(); edgeTab = null; }
                Activate(); FitScreen(); UpdateDesktopReservation();
            }
        }
        private async Task OpenWireless() { if (backgroundHidden || backgroundTransition || closingRequested || busy || connecting || preparing) return; using (var dialog = new WirelessForm(bridge)) dialog.ShowDialog(this); if (backgroundHidden || backgroundTransition || closingRequested || lifetime.IsCancellationRequested) return; await RefreshDevices(); }
        private void OpenGuide() { string guide = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "START-HERE.html"); if (File.Exists(guide)) Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true }); }
        private void SessionSwitch(object sender, SessionSwitchEventArgs e)
        { if (e.Reason == SessionSwitchReason.SessionLock) Ui(delegate { CancelOperation(); session.Stop(); SessionEnded(); SetStatus("Dock stopped because Windows was locked."); }); }
        private async void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (HandleBackgroundClosing(e)) return;
            if (finalClose) return;
            e.Cancel = true;
            if (closingRequested) return;
            if ((busy || proxyBusy || preparing) && MessageBox.Show(this, "Cancel the current copy and close the dock?", "IPMsg Dock", MessageBoxButtons.YesNo) != DialogResult.Yes) { exitRequested = false; return; }
            closingRequested = true; SetStatus("Closing…"); SaveWindowPreferences(); UpdateDesktopReservation();
            try
            {
                lifetime.Cancel(); CancelOperation(); StopBridge(); timer.Stop();
                SystemEvents.SessionSwitch -= SessionSwitch; session.Dispose();
                await AwaitBridgeCleanupAsync();
            }
            catch (Exception) { /* Best-effort remote cleanup has a bounded deadline. */ }
            finally
            {
                if (desktop != null) desktop.Dispose(); DisposeBridgeUi(); DisposeBackground();
                if (shortcuts != null) shortcuts.Dispose();
                if (fullControls != null) { fullControls.Close(); fullControls.Dispose(); fullControls = null; }
                if (edgeTab != null) { edgeTab.Close(); edgeTab.Dispose(); edgeTab = null; }
                finalClose = true; if (!IsDisposed) Close();
            }
        }
        internal static string Bytes(long bytes) { if (bytes >= 1073741824) return (bytes / 1073741824.0).ToString("0.0") + " GB"; if (bytes >= 1048576) return (bytes / 1048576.0).ToString("0.0") + " MB"; if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0") + " KB"; return bytes + " B"; }
    }
}
