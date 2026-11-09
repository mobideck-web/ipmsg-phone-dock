using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

namespace IPMsg.Dock
{
    internal sealed partial class DockForm
    {
        private NotifyIcon notificationIcon;
        private ContextMenuStrip notificationMenu;
        private bool backgroundHidden, backgroundTransition, restoreAfterBackground, exitRequested;
        private ShortcutGesture preferredShortcut = ShortcutGesture.Default;

        private void InitializeBackground()
        {
            if (notificationIcon != null) return;
            LoadShortcutPreference();
            notificationMenu = new ContextMenuStrip { BackColor = Card, ForeColor = ForeColor, ShowImageMargin = false };
            var show = notificationMenu.Items.Add("Show dock", null, delegate { ActivateDock(); });
            var hide = notificationMenu.Items.Add("Hide dock", null, async delegate { await HideToBackgroundAsync(); });
            var connection = notificationMenu.Items.Add("Connect phone", null, async delegate
            {
                ActivateDock();
                if (backgroundTransition || closingRequested) return;
                if (session.IsRunning) await Disconnect(); else await Connect();
            });
            notificationMenu.Items.Add("Shortcuts & startup…", null, delegate { ActivateDock(); ShowShortcutSettings(); });
            notificationMenu.Items.Add(new ToolStripSeparator());
            notificationMenu.Items.Add("Exit IPMsg Dock", null, delegate { RequestExit(); });
            notificationMenu.Opening += delegate
            {
                bool available = !backgroundTransition && !closingRequested;
                show.Enabled = !closingRequested; hide.Enabled = available && !backgroundHidden;
                connection.Text = session.IsRunning ? "Disconnect phone" : "Connect phone";
                connection.Enabled = available && !busy && !proxyBusy && !connecting && !preparing;
            };
            notificationIcon = new NotifyIcon { Icon = Icon, Text = "IPMsg Phone Dock", ContextMenuStrip = notificationMenu, Visible = true };
            notificationIcon.DoubleClick += delegate { ActivateDock(); };
            Resize += delegate { HandleBackgroundResize(); };
            Disposed += delegate { DisposeBackground(); };
        }

        private void HandleBackgroundResize()
        {
            if (notificationIcon != null && WindowState == FormWindowState.Minimized && !backgroundHidden && !closingRequested)
            { var ignored = HideToBackgroundAsync(); }
        }

        private bool HandleBackgroundClosing(FormClosingEventArgs args)
        {
            if (finalClose || exitRequested || args.CloseReason != CloseReason.UserClosing) return false;
            args.Cancel = true;
            var ignored = HideToBackgroundAsync();
            return true;
        }

        private async Task HideToBackgroundAsync()
        {
            if (IsDisposed || closingRequested || backgroundHidden || backgroundTransition) return;
            InitializeBackground();
            backgroundTransition = true;
            SaveWindowPreferences();
            backgroundHidden = true; restoreAfterBackground = false;
            UpdateDesktopReservation();
            try
            {
                // Cancel app-owned work before closing its screen and private file tunnel.
                // Restoring the window never starts another phone session automatically.
                CloseQuickFlyouts();
                CancelOperation(); StopBridge();
                stopping = true;
                try { session.Stop(); SessionEnded(); }
                finally { stopping = false; }
                timer.Stop();
                if (edgeTab != null) { edgeTab.Close(); edgeTab.Dispose(); edgeTab = null; }
                if (collapsed) { collapsed = false; Bounds = expandedBounds; }
                if (fullControls != null) fullControls.Hide();
                ShowInTaskbar = false; Hide();
                if (notificationIcon != null) notificationIcon.Text = "IPMsg Dock · paused in background";
                await AwaitBridgeCleanupAsync();
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while ((busy || preparing || connecting) && !closingRequested && DateTime.UtcNow < deadline) await Task.Delay(50);
                if (!IsDisposed && !closingRequested) SetStatus("Paused in background. Connect when ready.");
            }
            finally
            {
                backgroundTransition = false;
                if (restoreAfterBackground && !IsDisposed && !closingRequested) { restoreAfterBackground = false; ActivateDock(); }
            }
        }

        private bool PrepareBackgroundRestore()
        {
            if (backgroundTransition) { restoreAfterBackground = true; return false; }
            // ShowInTaskbar can recreate the native handle and raise Resize.
            // Normalize first while the hidden guard still suppresses minimize.
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            ShowInTaskbar = true; backgroundHidden = false;
            if (notificationIcon != null) notificationIcon.Text = "IPMsg Phone Dock";
            timer.Start(); return true;
        }

        private void ToggleDockVisibility()
        {
            if (!Visible || backgroundHidden || collapsed || WindowState == FormWindowState.Minimized) ActivateDock();
            else { var ignored = HideToBackgroundAsync(); }
        }

        private void RequestExit()
        { if (closingRequested || IsDisposed) return; exitRequested = true; Close(); }

        private void DisposeBackground()
        {
            if (notificationIcon != null) { notificationIcon.Visible = false; notificationIcon.Dispose(); notificationIcon = null; }
            if (notificationMenu != null) { notificationMenu.Dispose(); notificationMenu = null; }
        }

        private void LoadShortcutPreference()
        {
            string path = Path.Combine(stateDirectory, "shortcuts.xml");
            try
            {
                if (!File.Exists(path)) return;
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096 };
                var document = new XmlDocument { XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(path, settings)) document.Load(reader);
                XmlElement root = document.DocumentElement;
                if (root == null || root.Name != "shortcuts") return;
                if (root.GetAttribute("enabled") == "false") { preferredShortcut = null; return; }
                ShortcutGesture parsed; string error;
                if (ShortcutGesture.TryParse(root.GetAttribute("toggle"), out parsed, out error)) preferredShortcut = parsed;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (XmlException) { }
        }

        private void SaveShortcutPreference(ShortcutGesture gesture)
        {
            string path = Path.Combine(stateDirectory, "shortcuts.xml"), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (XmlWriter writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true }))
                {
                    writer.WriteStartElement("shortcuts"); writer.WriteAttributeString("enabled", gesture == null ? "false" : "true");
                    writer.WriteAttributeString("toggle", gesture == null ? "" : gesture.ToString()); writer.WriteEndElement();
                }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        private void ShowShortcutSettings()
        {
            if (backgroundTransition || closingRequested) return;
            InitializeDesktop();
            using (var dialog = new Form { Text = "Shortcuts & startup", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, ClientSize = new Size(410, 315), MinimizeBox = false,
                MaximizeBox = false, ShowInTaskbar = false, BackColor = Background, ForeColor = ForeColor, Font = Font })
            {
                var enabled = new CheckBox { Text = "Global shortcut to show / hide dock", Checked = preferredShortcut != null, Left = 18, Top = 18, Width = 370, Height = 26 };
                var binding = new TextBox { Text = preferredShortcut == null ? "Ctrl+Alt+I" : preferredShortcut.ToString(), Left = 20, Top = 51, Width = 366, BackColor = Card, ForeColor = ForeColor };
                var hint = new Label { Text = "Examples: Ctrl+Alt+I, Ctrl+Shift+F8, Win+Alt+D\nF11: fullscreen   ·   Esc: leave fullscreen   ·   F6: options", Left = 18, Top = 84, Width = 374, Height = 44, ForeColor = GlassTheme.MutedText };
                bool startup = StartupPreference.IsEnabled(Application.ExecutablePath);
                var start = new CheckBox { Text = "Start with Windows, quietly in background", Checked = startup, Left = 18, Top = 138, Width = 374, Height = 26 };
                var detail = new Label { Text = "Background pauses the phone screen and file sharing.\nUse the tray icon or shortcut, then Connect to resume.", Left = 18, Top = 174, Width = 374, Height = 42, ForeColor = GlassTheme.MutedText };
                var message = new Label { Left = 18, Top = 219, Width = 374, Height = 40, ForeColor = GlassTheme.Danger };
                var save = Button("Save", delegate
                {
                    ShortcutGesture selected = null; string error;
                    if (enabled.Checked && !ShortcutGesture.TryParse(binding.Text, out selected, out error)) { message.Text = error; return; }
                    try
                    {
                        // Persist while the previous binding is still registered. Any
                        // failure releases only the candidate and retains that binding.
                        if (!desktop.TrySetShortcut(selected, delegate
                        {
                            bool startupChanged = false;
                            try
                            {
                                if (start.Checked != startup) { StartupPreference.SetEnabled(Application.ExecutablePath, start.Checked); startupChanged = true; }
                                SaveShortcutPreference(selected);
                            }
                            catch
                            {
                                if (startupChanged) StartupPreference.SetEnabled(Application.ExecutablePath, startup);
                                throw;
                            }
                        }, out error)) { message.Text = error; return; }
                        preferredShortcut = selected;
                        SetStatus("Shortcut and startup preferences saved."); dialog.DialogResult = DialogResult.OK;
                    }
                    catch (Exception failure) { message.Text = "Could not save preferences: " + failure.Message; }
                });
                save.Location = new Point(310, 271); var dismiss = Button("Cancel", delegate { dialog.DialogResult = DialogResult.Cancel; }); dismiss.Location = new Point(224, 271);
                enabled.CheckedChanged += delegate { binding.Enabled = enabled.Checked; }; binding.Enabled = enabled.Checked;
                dialog.Controls.AddRange(new Control[] { enabled, binding, hint, start, detail, message, dismiss, save });
                dialog.AcceptButton = save; dialog.CancelButton = dismiss; dialog.ShowDialog(this);
            }
        }
    }

    internal static class StartupPreference
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "IPMsgDock";
        internal static string Command(string executable) { return "\"" + Path.GetFullPath(executable) + "\" --background"; }
        internal static bool IsEnabled(string executable)
        {
            try { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false)) return key != null && String.Equals(key.GetValue(ValueName) as string, Command(executable), StringComparison.OrdinalIgnoreCase); }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        internal static void SetEnabled(string executable, bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            { if (enabled) key.SetValue(ValueName, Command(executable), RegistryValueKind.String); else key.DeleteValue(ValueName, false); }
        }
    }
}
