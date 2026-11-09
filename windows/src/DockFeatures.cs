using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

namespace IPMsg.Dock
{
    internal sealed partial class DockForm
    {
        private Connections connections;
        private DesktopIntegration desktop;
        private bool fullscreen, screenOff, preparing, compactView, dockedRight = true, glassAllowed = true;
        private Rectangle windowedBounds;
        private bool windowedTopMost;
        private OverlayForm fullControls;
        private DateTime fullscreenSince;
        private LocalShortcuts shortcuts;
        private Button fullscreenButton, moreButton;
        private string currentIdentity;
        private string widthPreset = "narrow";

        private void PositionEntryActions()
        {
            int left = Math.Max(0, (preview.ClientSize.Width - connect.Width) / 2);
            int top = Math.Max(70, preview.ClientSize.Height / 2 + 10);
            connect.Location = new Point(left, top);
            wireless.Location = new Point(left, top + connect.Height + 8);
        }

        private async Task ChoosePhone()
        {
            if (busy || proxyBusy || connecting || preparing || session.IsRunning) return;
            await RefreshDevices();
            if (backgroundHidden || backgroundTransition || closingRequested || lifetime.IsCancellationRequested) return;
            if (phones.Items.Count == 0) { SetStatus("Connect by USB, or choose Set up Wi-Fi."); return; }
            using (var picker = new Form { Text = "Choose phone", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, ClientSize = new Size(365, 160), MinimizeBox = false,
                MaximizeBox = false, ShowInTaskbar = false, BackColor = Background, ForeColor = ForeColor, Font = Font })
            {
                var list = new ComboBox { Left = 16, Top = 22, Width = 330 };
                StyleCombo(list); foreach (var item in phones.Items) list.Items.Add(item);
                list.SelectedIndex = Math.Max(0, phones.SelectedIndex);
                var use = Button("Use phone", delegate { picker.DialogResult = DialogResult.OK; });
                use.Location = new Point(245, 92); picker.AcceptButton = use;
                picker.Controls.Add(list); picker.Controls.Add(use);
                if (picker.ShowDialog(this) == DialogResult.OK) phones.SelectedIndex = list.SelectedIndex;
            }
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg != 0x84 || fullscreen || dockedRight || WindowState != FormWindowState.Normal || message.Result != (IntPtr)1) return;
            long position = message.LParam.ToInt64();
            Point point = PointToClient(new Point(unchecked((short)(position & 0xffff)), unchecked((short)((position >> 16) & 0xffff))));
            int edge = Math.Max(5, (int)(6 * DeviceDpi / 96.0));
            bool left = point.X < edge, right = point.X >= ClientSize.Width - edge;
            bool top = point.Y < edge, bottom = point.Y >= ClientSize.Height - edge;
            message.Result = (IntPtr)(top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 1);
        }

        private void InitializeFeatures()
        {
            connections = new Connections(bridge, stateDirectory);
            DoubleBuffered = true; GlassTheme.Apply(this); layout.BackColor = Color.Transparent;
            LoadWindowPreferences(); NativeGlass.SetAllowed(this, glassAllowed);
            shortcuts = new LocalShortcuts(() => IsDisposed ? IntPtr.Zero : Handle, key =>
            {
                if (key == Keys.F6 && !fullscreen) { Ui(delegate { moreButton.Focus(); }); return true; }
                if (key == Keys.F11 || key == Keys.Escape && fullscreen)
                { Ui(delegate { SetFullscreen(key == Keys.F11 ? !fullscreen : false); }); return true; }
                return false;
            });
            ResizeEnd += delegate { widthPreset = "custom"; SaveWindowPreferences(); };
            ApplyChrome();
        }

        private void InitializeDesktop()
        {
            InitializeBackground();
            if (desktop == null) desktop = new DesktopIntegration(this, ActivateDock, ToggleDockVisibility, delegate(string text) { SetStatus(text); }, preferredShortcut);
            NativeGlass.SetAllowed(this, glassAllowed); UpdateDesktopReservation();
        }
        private void UpdateDesktopReservation()
        { if (desktop != null && !IsDisposed) desktop.ReserveRight(dockedRight, Width, closingRequested || fullscreen || collapsed || backgroundHidden || backgroundTransition); }
        private void ActivateDock()
        {
            if (IsDisposed || closingRequested) return;
            if (!PrepareBackgroundRestore()) return;
            if (collapsed) ToggleCollapse();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show(); BringToFront(); Activate(); UpdateDesktopReservation();
            if (session.IsRunning) moreButton.Focus(); else connect.Focus();
        }
        private void BeginDockMove()
        {
            dockedRight = false; UpdateDesktopReservation();
            Native.BeginMove(Handle); SaveWindowPreferences();
        }

        protected override void OnPaintBackground(PaintEventArgs e) { NativeGlass.PaintBackground(this, e); }
        private string QualityProfile() { return new[] { "balanced", "smooth", "sharp", "native", "cinema", "gaming90", "gaming120" }[Math.Max(0, quality.SelectedIndex)]; }

        private void ShowMore()
        {
            var menu = new ContextMenuStrip { BackColor = Card, ForeColor = ForeColor, ShowImageMargin = false };
            StylePopup(menu);
            bool changeAllowed = !busy && !proxyBusy && !connecting && !preparing;
            menu.Items.Add(session.IsRunning ? "Disconnect" : "Connect phone", null, async delegate { if (session.IsRunning) await Disconnect(); else await Connect(); }).Enabled = changeAllowed;
            var sharing = new ToolStripMenuItem("Files") { Enabled = session.IsRunning && changeAllowed };
            sharing.DropDownItems.Add("Choose files, photos or folder…", null, async delegate { await OpenBridgeViewAsync("files"); });
            sharing.DropDownItems.Add("Edge handoff", null, async delegate { await OpenBridgeViewAsync("edge"); });
            sharing.DropDownItems.Add("Share from another app…", null, delegate
            {
                MessageBox.Show(this, "On the phone, use an app's Share action and choose IPMsg's Dock option.\n\nOnly the file, image, text or link that app actually shares is available. Some apps share a preview or link instead of an original file. Edge handoff needs a source app that supports Android cross-app dragging.", "Share to IPMsg Dock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            sharing.DropDownItems.Add("Send files…", null, async delegate { await ChooseUpload(); }); menu.Items.Add(sharing);
            menu.Items.Add("Copy phone screenshot", null, async delegate { await CapturePhoneAsync(); }).Enabled = session.IsRunning && changeAllowed;

            var window = new ToolStripMenuItem("Window");
            window.DropDownItems.Add("Hide to notification area", null, async delegate { await HideToBackgroundAsync(); });
            window.DropDownItems.Add(new ToolStripMenuItem("Keep on top", null, delegate { TopMost = !TopMost; SaveWindowPreferences(); }) { Checked = TopMost });
            window.DropDownItems.Add(new ToolStripMenuItem("Dock to right · reserve desktop space", null, delegate { DockRight(); }) { Checked = dockedRight });
            window.DropDownItems.Add(new ToolStripMenuItem("Floating window", null, delegate { dockedRight = false; UpdateDesktopReservation(); SaveWindowPreferences(); }) { Checked = !dockedRight });
            var widthMenu = new ToolStripMenuItem("Dock width");
            widthMenu.DropDownItems.Add("Narrow · 280", null, delegate { SetDockWidth(280); });
            widthMenu.DropDownItems.Add("Comfortable · 360", null, delegate { SetDockWidth(360); });
            window.DropDownItems.Add(widthMenu);
            var transparency = new ToolStripMenuItem("Transparency");
            transparency.DropDownItems.Add(new ToolStripMenuItem("Glass background", null, delegate { SetGlassPreference(true); }) { Checked = glassAllowed });
            transparency.DropDownItems.Add(new ToolStripMenuItem("Solid background", null, delegate { SetGlassPreference(false); }) { Checked = !glassAllowed }); window.DropDownItems.Add(transparency);
            window.DropDownItems.Add("Collapse", null, delegate { ToggleCollapse(); });
            window.DropDownItems.Add("Shortcuts & startup…", null, delegate { ShowShortcutSettings(); });
            window.DropDownItems.Add("Create desktop shortcut", null, delegate
            {
                try { DesktopIntegration.CreateDesktopShortcut(Application.ExecutablePath); SetStatus("Desktop shortcut created."); }
                catch (Exception error) { SetStatus(error.Message, true); }
            });
            window.DropDownItems.Add(fullscreen ? "Exit fullscreen    Esc" : "Fullscreen    F11", null, delegate { SetFullscreen(!fullscreen); }).Enabled = session.IsRunning;
            menu.Items.Add(window);

            var stream = new ToolStripMenuItem("Stream");
            var settings = new ToolStripMenuItem("Stream quality") { Enabled = changeAllowed };
            for (int index = 0; index < quality.Items.Count; index++)
            {
                int selected = index;
                var item = new ToolStripMenuItem(quality.Items[index].ToString()) { Checked = quality.SelectedIndex == index };
                item.Click += delegate { quality.SelectedIndex = selected; RestartStream(); }; settings.DropDownItems.Add(item);
            }
            stream.DropDownItems.Add(settings);
            stream.DropDownItems.Add(new ToolStripMenuItem("Phone sound", null, delegate { audio.Checked = !audio.Checked; RestartStream(); }) { Checked = audio.Checked, Enabled = changeAllowed });
            stream.DropDownItems.Add(new ToolStripMenuItem("Phone display off", null, delegate { screenOff = !screenOff; RestartStream(); }) { Checked = screenOff, Enabled = changeAllowed && mode.SelectedIndex == 0 });
            stream.DropDownItems.Add(new ToolStripMenuItem("View only", null, delegate { mode.SelectedIndex = mode.SelectedIndex == 1 ? 0 : 1; RestartStream(); }) { Checked = mode.SelectedIndex == 1, Enabled = changeAllowed });
            stream.DropDownItems.Add("Wake phone / show lock screen", null, async delegate { await Navigation(224); }).Enabled = session.IsRunning && changeAllowed;
            menu.Items.Add(stream);

            var devices = new ToolStripMenuItem("Connection") { Enabled = changeAllowed && !session.IsRunning };
            devices.DropDownItems.Add("Choose phone…", null, async delegate { await ChoosePhone(); });
            devices.DropDownItems.Add("Set up Wi-Fi…", null, async delegate { await OpenWireless(); });
            devices.DropDownItems.Add("Forget reconnect approvals…", null, delegate
            {
                if (MessageBox.Show(this, "Forget the dock's remembered phones and connection approvals? Android debugging authorization is revoked separately on your phone.", "IPMsg Dock", MessageBoxButtons.OKCancel) == DialogResult.OK)
                { connections.Forget(); SetStatus("Remembered reconnect approvals cleared."); }
            });
            menu.Items.Add(devices); menu.Items.Add("Setup guide", null, delegate { OpenGuide(); });
            menu.Items.Add("Exit IPMsg Dock", null, delegate { RequestExit(); });
            menu.Closed += delegate { Ui(delegate { menu.Dispose(); }); }; menu.Show(moreButton, new Point(0, moreButton.Height));
        }

        private async Task ChooseUpload()
        {
            if (!CanTransfer()) return;
            using (var chooser = new OpenFileDialog { Multiselect = true, Title = "Copy files to your phone" })
                if (chooser.ShowDialog(this) == DialogResult.OK) await Upload(chooser.FileNames);
        }
        private void SetDockWidth(int logicalWidth)
        {
            if (fullscreen) SetFullscreen(false);
            widthPreset = logicalWidth <= 280 ? "narrow" : "comfortable";
            int desired = Math.Max(MinimumSize.Width, (int)Math.Round(logicalWidth * DeviceDpi / 96.0));
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int actual = Math.Min(desired, area.Width);
            if (dockedRight) { Width = actual; UpdateDesktopReservation(); }
            else Bounds = new Rectangle(Math.Max(area.Left, Math.Min(Left, area.Right - actual)), Top, actual, Height);
            SaveWindowPreferences();
        }
        private void SetGlassPreference(bool allowed)
        {
            glassAllowed = allowed; NativeGlass.SetAllowed(this, allowed);
            if (fullControls != null) NativeGlass.SetAllowed(fullControls, allowed);
            SaveWindowPreferences();
        }

        private async Task CapturePhoneAsync()
        {
            if (!CanTransfer()) return;
            string selected = serial;
            busy = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            CancellationToken token = operation.Token; UpdateControls();
            try
            {
                SetStatus("Capturing phone screen…");
                using (Bitmap bitmap = await PhoneScreenshot.CaptureAsync(bridge, selected, token))
                {
                    if (serial != selected || !session.IsRunning) throw new OperationCanceledException();
                    await PhoneScreenshot.SetClipboardAsync(bitmap, token);
                    SetStatus("Phone screenshot copied to clipboard.");
                }
            }
            catch (OperationCanceledException) { SetStatus("Capture cancelled."); }
            catch (Exception error) { SetStatus(error.Message, true); }
            finally { EndOperation(); }
        }

        private void ApplyChrome()
        {
            if (layout.RowStyles.Count < 6) return;
            bool active = session.IsRunning;
            UpdateQuickControls();
            compactView = active && !fullscreen;
            layout.SuspendLayout();
            int[] heights = { 46, 0, active ? 42 : 0, active && trayOpen ? 32 : 0, active && trayOpen ? 190 : 0, 36 };
            for (int row = 0; row < 6; row++)
            {
                if (row == 1) continue;
                layout.RowStyles[row].Height = fullscreen ? 0 : heights[row];
                foreach (Control control in layout.Controls) if (layout.GetRow(control) == row) control.Visible = !fullscreen && heights[row] > 0;
            }
            layout.Padding = fullscreen ? Padding.Empty : new Padding(6, 3, 6, 5);
            connect.Visible = wireless.Visible = !active && !fullscreen;
            preview.BackColor = active ? Color.Black : Color.Transparent;
            preview.Margin = fullscreen ? Padding.Empty : new Padding(0, 3, 0, 3);
            PositionEntryActions(); layout.ResumeLayout(); FitScreen();
        }

        private void RestartStream()
        {
            if (!session.IsRunning) return;
            if (busy || proxyBusy || connecting || preparing) { SetStatus("Finish or cancel the copy before changing stream options."); return; }
            string selected = serial;
            try
            {
                session.Stop();
                windowTitle = "IPMsgDock-" + Guid.NewGuid().ToString("N"); screenWindow = IntPtr.Zero;
                placeholder.Visible = true; placeholder.Text = "Updating stream…";
                child = session.Start(selected, windowTitle, mode.SelectedIndex == 1, audio.Checked, QualityProfile(), screenOff && mode.SelectedIndex == 0);
                startupTime = DateTime.UtcNow; proxyStatus = ""; UpdateControls();
            }
            catch (Exception error) { session.Stop(); SessionEnded(); SetStatus(error.Message, true); }
        }

        private void SetFullscreen(bool enabled)
        {
            if (fullscreen == enabled || connecting || collapsed || enabled && !session.IsRunning) return;
            IntPtr saved = screenWindow;
            if (saved != IntPtr.Zero && Native.IsWindow(saved)) Native.Park(saved);
            try
            {
                SuspendLayout();
                if (enabled)
                {
                    windowedBounds = Bounds; windowedTopMost = TopMost;
                    fullscreen = true; UpdateDesktopReservation(); // Release before taking monitor-sized bounds.
                    Rectangle bounds = Screen.FromControl(this).Bounds;
                    FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Normal; Bounds = bounds; TopMost = true;
                    fullscreenSince = DateTime.UtcNow;
                    fullControls = new OverlayForm { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
                        BackColor = Card, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(bounds.Right - 205, bounds.Top + 10, 195, 36) };
                    var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(2) };
                    controls.Controls.Add(Button("Exit fullscreen", delegate { SetFullscreen(false); }));
                    controls.Controls.Add(Button("Cancel", delegate { CancelOperation(); })); fullControls.Controls.Add(controls);
                    NativeGlass.SetAllowed(fullControls, glassAllowed);
                }
                else
                {
                    if (fullControls != null) { fullControls.Close(); fullControls.Dispose(); fullControls = null; }
                    FormBorderStyle = FormBorderStyle.None; TopMost = windowedTopMost; Bounds = windowedBounds;
                    fullscreen = false; UpdateDesktopReservation(); // Restore first, then resume the AppBar.
                }
                ApplyChrome(); ResumeLayout(true); NativeGlass.Apply(this);
            }
            finally
            {
                if (saved != IntPtr.Zero && Native.IsWindow(saved)) { Native.Embed(saved, preview.Handle); screenWindow = saved; FitScreen(); Native.SetFocus(saved); }
            }
        }
        private void UpdateFullscreenControls()
        {
            if (!fullscreen || fullControls == null) return;
            bool near = MousePosition.Y <= Top + 52 && MousePosition.X >= Left && MousePosition.X < Right;
            bool visible = near || fullControls.Bounds.Contains(MousePosition) || (DateTime.UtcNow - fullscreenSince).TotalSeconds < 3;
            if (visible && !fullControls.Visible) { fullControls.Show(); NativeGlass.Apply(fullControls); }
            else if (!visible && fullControls.Visible) fullControls.Hide();
        }

        private void LoadWindowPreferences()
        {
            string path = Path.Combine(stateDirectory, "window.xml");
            if (!File.Exists(path)) return;
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8192 };
                XmlDocument document = new XmlDocument { XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(path, settings)) document.Load(reader);
                XmlElement root = document.DocumentElement;
                if (root == null || root.Name != "window") return;
                dockedRight = root.GetAttribute("docked") != "false"; glassAllowed = root.GetAttribute("glass") != "false";
                string savedPreset = root.GetAttribute("width-preset");
                widthPreset = String.IsNullOrEmpty(savedPreset) ? "narrow" : savedPreset;
                TopMost = root.GetAttribute("topmost") == "true";
                int left, top, width, height;
                if (Int32.TryParse(root.GetAttribute("left"), out left) && Int32.TryParse(root.GetAttribute("top"), out top) &&
                    Int32.TryParse(root.GetAttribute("width"), out width) && Int32.TryParse(root.GetAttribute("height"), out height))
                {
                    // Earlier releases saved a wide default without recording an
                    // explicit width choice. Migrate that once to the narrow default.
                    if (String.IsNullOrEmpty(savedPreset)) width = (int)Math.Round(280 * DeviceDpi / 96.0);
                    Rectangle wanted = new Rectangle(left, top, Math.Max(1, Math.Min(8192, width)), Math.Max(1, Math.Min(8192, height)));
                    Rectangle area = Screen.FromRectangle(wanted).WorkingArea;
                    width = Math.Min(area.Width, Math.Max(MinimumSize.Width, wanted.Width));
                    height = Math.Min(area.Height, Math.Max(MinimumSize.Height, wanted.Height));
                    Bounds = new Rectangle(Math.Max(area.Left, Math.Min(left, area.Right - width)), Math.Max(area.Top, Math.Min(top, area.Bottom - height)), width, height);
                }
            }
            catch (XmlException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private void SaveWindowPreferences()
        {
            if (fullscreen || collapsed || WindowState == FormWindowState.Minimized) return;
            string path = Path.Combine(stateDirectory, "window.xml"), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (XmlWriter writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true }))
                {
                    writer.WriteStartElement("window"); writer.WriteAttributeString("docked", dockedRight ? "true" : "false");
                    writer.WriteAttributeString("width-preset", widthPreset);
                    writer.WriteAttributeString("glass", glassAllowed ? "true" : "false"); writer.WriteAttributeString("topmost", TopMost ? "true" : "false");
                    writer.WriteAttributeString("left", Left.ToString(CultureInfo.InvariantCulture)); writer.WriteAttributeString("top", Top.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("width", Width.ToString(CultureInfo.InvariantCulture)); writer.WriteAttributeString("height", Height.ToString(CultureInfo.InvariantCulture)); writer.WriteEndElement();
                }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            catch (IOException) { SetStatus("Window preferences could not be saved.", true); }
            catch (UnauthorizedAccessException) { SetStatus("Window preferences could not be saved.", true); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
}
