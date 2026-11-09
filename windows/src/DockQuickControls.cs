using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IPMsg.Dock
{
    internal sealed partial class DockForm
    {
        private DockIconButton soundIcon, displayIcon;
        private QuickFlyout windowActions, volumeActions;
        private readonly ToolTip quickTips = new ToolTip { InitialDelay = 350 };

        private DockIconButton IconButton(string symbol, string name, EventHandler action)
        {
            var button = new DockIconButton { Symbol = symbol, AccessibleName = name,
                Size = new Size(34, 32), Margin = new Padding(2), AutoSize = false,
                Quiet = true, CornerRadius = 8 };
            button.Click += action; quickTips.SetToolTip(button, name); return button;
        }
        private Control CreatePhoneSurface()
        {
            soundIcon = IconButton("mute", "Phone sound · hover for volume", delegate { TogglePhoneSound(); });
            displayIcon = IconButton("display", "Phone display on/off", async delegate { await TogglePhoneDisplay(); });
            var header=fullscreenButton.Parent;
            foreach (var icon in new[] { soundIcon, displayIcon }) { icon.Size = new Size(28,32); header.Controls.Add(icon); }
            header.Controls.SetChildIndex(soundIcon,1); header.Controls.SetChildIndex(displayIcon,2);
            soundIcon.MouseEnter += delegate { ShowPhoneVolume(); };
            soundIcon.KeyDown += delegate(object sender, KeyEventArgs args) {
                if (args.KeyCode != Keys.Down) return;
                args.SuppressKeyPress = true; ShowPhoneVolume();
                if (volumeActions != null) volumeActions.FocusFirst();
            };
            FormClosed += delegate { CloseQuickFlyouts(); quickTips.Dispose(); };
            LocationChanged += delegate { CloseQuickFlyouts(); };
            return preview;
        }
        private void UpdateQuickControls()
        {
            if (soundIcon == null) return;
            soundIcon.Visible = displayIcon.Visible = session.IsRunning && !fullscreen;
            bool available = session.IsRunning && !busy && !preparing && !proxyBusy && !connecting;
            soundIcon.Enabled = available; displayIcon.Enabled = available && mode.SelectedIndex == 0;
            soundIcon.Symbol = audio.Checked ? "sound" : "mute"; soundIcon.Active = audio.Checked;
            displayIcon.Active = !screenOff; soundIcon.Invalidate(); displayIcon.Invalidate();
        }
        private void CloseQuickFlyouts()
        {
            if (windowActions != null) { windowActions.Close(); windowActions = null; }
            if (volumeActions != null) { volumeActions.Close(); volumeActions = null; }
        }
        private void ShowWindowActions(bool keyboard)
        {
            if (closingRequested || fullscreen || IsDisposed) return;
            if (windowActions != null && !windowActions.IsDisposed) { if (keyboard) windowActions.FocusFirst(); return; }
            var actions = new QuickFlyout(this, fullscreenButton, glassAllowed);
            windowActions = actions;
            actions.Add(IconButton("full", "Fullscreen · F11", delegate { actions.Close(); SetFullscreen(true); })).Enabled = session.IsRunning;
            actions.Add(IconButton("top", "Keep on top", delegate { TopMost = !TopMost; SaveWindowPreferences(); actions.Close(); }), TopMost);
            actions.Add(IconButton("pin", "Pin/unpin right · reserve desktop space", delegate {
                if(dockedRight) { dockedRight=false; UpdateDesktopReservation(); SaveWindowPreferences(); } else DockRight(); actions.Close();
            }), dockedRight);
            actions.Add(IconButton("collapse", "Collapse dock", delegate { actions.Close(); ToggleCollapse(); }));
            actions.Open(); if (keyboard) actions.FocusFirst();
        }
        private void TogglePhoneSound()
        {
            if (!session.IsRunning || busy || proxyBusy || connecting || preparing) return;
            audio.Checked = !audio.Checked; RestartStream(); UpdateQuickControls();
        }
        private async Task TogglePhoneDisplay()
        {
            if (!session.IsRunning || mode.SelectedIndex != 0 || busy || proxyBusy || connecting || preparing) return;
            screenOff = !screenOff; RestartStream(); UpdateQuickControls();
            if (!screenOff && serial != null) await Navigation(224); // Wakes; never dismisses Android's lock screen.
        }
        private void ShowPhoneVolume()
        {
            if (!session.IsRunning || !soundIcon.Enabled || fullscreen || closingRequested) return;
            if (volumeActions != null && !volumeActions.IsDisposed) return;
            var actions = new QuickFlyout(this, soundIcon, glassAllowed); volumeActions = actions;
            actions.AddText("Phone volume");
            actions.Add(IconButton("minus", "Volume down", async delegate { await Navigation(25); }));
            actions.Add(IconButton("plus", "Volume up", async delegate { await Navigation(24); }));
            actions.Add(IconButton("mute", "Mute/unmute phone media", async delegate { await Navigation(164); }));
            actions.Open();
        }
    }

    internal sealed class QuickFlyout : Form
    {
        private readonly Control anchor;
        private readonly FlowLayoutPanel row;
        private readonly Timer timer = new Timer { Interval = 180 };
        private int outside;
        protected override bool ShowWithoutActivation { get { return true; } }
        public QuickFlyout(Form owner, Control anchor, bool glass)
        {
            this.anchor = anchor; Owner = owner; Text = "IPMsg · Quick controls"; FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = owner.TopMost; StartPosition = FormStartPosition.Manual;
            BackColor = GlassTheme.Card; ForeColor = GlassTheme.Text; Font = owner.Font;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(6);
            row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent };
            Controls.Add(row); NativeGlass.SetAllowed(this, glass);
            timer.Tick += delegate {
                if (anchor.IsDisposed || Owner == null || !Owner.Visible || Owner.WindowState == FormWindowState.Minimized) { Close(); return; }
                bool inside = Bounds.Contains(Cursor.Position) || anchor.RectangleToScreen(anchor.ClientRectangle).Contains(Cursor.Position);
                if (inside || ContainsFocus) outside = 0; else if (++outside >= 3) Close();
            };
            FormClosed += delegate { timer.Stop(); timer.Dispose(); };
            KeyPreview = true; KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
        }
        protected override void OnPaintBackground(PaintEventArgs e) { NativeGlass.PaintBackground(this, e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = GlassTheme.RoundRectangle(new RectangleF(.5f, .5f, Width - 1, Height - 1), SystemInformation.HighContrast ? 0 : 12 * e.Graphics.DpiX / 96f))
            using (var border = new Pen(SystemInformation.HighContrast ? SystemColors.WindowFrame : Color.FromArgb(34, Color.White))) e.Graphics.DrawPath(border, path);
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width < 2 || Height < 2 || SystemInformation.HighContrast) return;
            using (var path = GlassTheme.RoundRectangle(new RectangleF(0, 0, Width, Height), 12 * DeviceDpi / 96f))
            {
                Region previous = Region; Region = new Region(path); if (previous != null) previous.Dispose();
            }
        }
        public DockIconButton Add(DockIconButton button, bool active = false) { button.Active = active; row.Controls.Add(button); return button; }
        public void AddText(string text) { row.Controls.Add(new Label { Text = text, AutoSize = true, Padding = new Padding(5, 10, 5, 0), ForeColor = GlassTheme.MutedText }); }
        public void Open()
        {
            PerformLayout();
            Rectangle dock=Owner.RectangleToScreen(Owner.ClientRectangle);
            Rectangle button=anchor.RectangleToScreen(anchor.ClientRectangle);
            Location = Placement(dock,button,Size);
            Show(Owner); timer.Start();
        }
        internal static Point Placement(Rectangle dock,Rectangle button,Size popup)
        {
            // The Shell work area excludes a pinned dock; clamping to it would eject
            // the flyout into other apps. Keep it inside its own dock below the icon.
            int left=dock.Left+6,right=dock.Right-popup.Width-6;
            int x=button.Left+(button.Width-popup.Width)/2;
            int y=button.Bottom+5;
            return new Point(Math.Max(left,Math.Min(x,Math.Max(left,right))),Math.Max(dock.Top+6,Math.Min(y,dock.Bottom-popup.Height-6)));
        }
        public void FocusFirst() { Activate(); SelectNextControl(null, true, true, true, true); }
    }

    internal sealed class DockIconButton : GlassButton
    {
        public string Symbol = "layout";
        public bool Active;
        protected override void OnPaint(PaintEventArgs e)
        {
            Text = ""; base.OnPaint(e);
            if(LucideIcons.Draw(e.Graphics,ClientRectangle,Symbol,!Enabled ? GlassTheme.MutedText : Active ? GlassTheme.Accent : ForeColor)) return;
            Graphics g = e.Graphics; GraphicsState state = g.Save();
            try {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = Math.Min(Width, Height) / 28f; g.TranslateTransform(Width / 2f, Height / 2f); g.ScaleTransform(scale, scale);
                using (var pen = new Pen(!Enabled ? GlassTheme.MutedText : Active ? GlassTheme.Accent : ForeColor, 1.4f)) {
                    pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    if (Symbol == "full") { g.DrawLines(pen,new[]{new Point(-3,-8),new Point(-8,-8),new Point(-8,-3)});g.DrawLines(pen,new[]{new Point(3,-8),new Point(8,-8),new Point(8,-3)});g.DrawLines(pen,new[]{new Point(-8,3),new Point(-8,8),new Point(-3,8)});g.DrawLines(pen,new[]{new Point(3,8),new Point(8,8),new Point(8,3)}); }
                    else if (Symbol == "sound" || Symbol == "mute") { g.DrawPolygon(pen,new[]{new Point(-8,-3),new Point(-4,-3),new Point(1,-7),new Point(1,7),new Point(-4,3),new Point(-8,3)}); if(Symbol=="sound") {g.DrawArc(pen,0,-5,9,10,-65,130);g.DrawArc(pen,-1,-8,16,16,-65,130);} else {g.DrawLine(pen,5,-3,10,3);g.DrawLine(pen,5,3,10,-3);} }
                    else if (Symbol == "display") { g.DrawRectangle(pen,-5,-9,10,18);g.DrawLine(pen,-1,6,1,6);if(!Active)g.DrawLine(pen,-8,8,8,-8); }
                    else if (Symbol == "plus" || Symbol == "minus") {g.DrawLine(pen,-6,0,6,0);if(Symbol=="plus")g.DrawLine(pen,0,-6,0,6);}
                    else if (Symbol == "top") {g.DrawRectangle(pen,-8,-6,16,13);g.DrawLine(pen,-8,-2,8,-2);g.DrawLine(pen,0,-10,0,-5);}
                    else if (Symbol == "collapse") {g.DrawRectangle(pen,-8,-7,16,14);g.DrawLines(pen,new[]{new Point(-2,-3),new Point(2,0),new Point(-2,3)});}
                    else {g.DrawRectangle(pen,-8,-7,16,14);g.DrawLine(pen,3,-7,3,7);if(Symbol=="pin")g.DrawLine(pen,5,-4,5,4);}
                }
            } finally { g.Restore(state); }
        }
    }
}
