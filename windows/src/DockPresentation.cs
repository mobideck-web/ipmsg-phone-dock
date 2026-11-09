using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace IPMsg.Dock
{
    internal sealed partial class DockForm
    {
        // One quiet material for the shell, with a single raised control bar. Video stays opaque.
        private void InitializePresentation()
        {
            SuspendLayout();
            var header = layout.GetControlFromPosition(0, 0) as FlowLayoutPanel;
            var navigation = layout.GetControlFromPosition(0, 2) as FlowLayoutPanel;
            if (header != null)
            {
                Label brand = header.Controls.OfType<Label>().FirstOrDefault();
                if (brand != null)
                {
                    brand.Text = ""; brand.Padding = Padding.Empty;
                    brand.AccessibleName = "IPMsg Phone Dock";
                    var mark = new DockBrandMark { Dock = DockStyle.Fill };
                    mark.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left && !fullscreen) BeginDockMove(); };
                    brand.Controls.Add(mark);
                }
                foreach (GlassButton button in header.Controls.OfType<GlassButton>())
                { button.Quiet = true; button.CornerRadius = 8; }
            }
            if (navigation != null)
            {
                navigation.Padding = new Padding(3, 4, 3, 4);
                navigation.Paint += PaintNavigationBar;
                back.Size = home.Size = new Size(29, 30);
                back.Margin = home.Margin = new Padding(0, 0, 1, 0);
                foreach (GlassButton button in navigation.Controls.OfType<GlassButton>())
                {
                    button.Quiet = true; button.CornerRadius = 8;
                    button.Font = new Font("Segoe UI", 8.5f);
                    button.Height = 30;
                    button.Margin = new Padding(1, 0, 1, 0);
                }
                trayToggle.AutoSize = false; trayToggle.Width = 114;
                trayToggle.Text = "Files"; ((GlassButton)trayToggle).IconName = "files";
                captureButton.AutoSize = false; captureButton.Width = captureButton is DockIconButton ? 34 : 70;
                navigation.Layout += delegate
                {
                    float scale = navigation.DeviceDpi / 96f;
                    int occupied = back.Width + home.Width + back.Margin.Horizontal + home.Margin.Horizontal +
                        captureButton.Width + captureButton.Margin.Horizontal + trayToggle.Margin.Horizontal + navigation.Padding.Horizontal;
                    int width = Math.Max((int)(80 * scale), navigation.ClientSize.Width - occupied);
                    if (trayToggle.Width != width) trayToggle.Width = width;
                };
            }
            ((GlassButton)connect).CornerRadius = 11;
            connect.Font = new Font("Segoe UI Semibold", 9.5f);
            ((GlassButton)wireless).Quiet = true;
            ((GlassButton)wireless).CornerRadius = 9;
            wireless.ForeColor = GlassTheme.MutedText;
            wireless.Font = new Font("Segoe UI", 9);
            status.Font = new Font("Segoe UI", 8);
            status.Padding = new Padding(6, 0, 0, 0);
            status.TextAlign = ContentAlignment.MiddleLeft;
            var footer = layout.GetControlFromPosition(0, 5) as TableLayoutPanel;
            if (footer != null && footer.ColumnStyles.Count > 1)
            {
                EventHandler sizeFooter = delegate { footer.ColumnStyles[1].Width = cancel.Visible ? 88 * DeviceDpi / 96f : 0; };
                cancel.VisibleChanged += sizeFooter; sizeFooter(null, EventArgs.Empty);
            }
            status.TextChanged += delegate { quickTips.SetToolTip(status, status.Text); status.AccessibleDescription = status.Text; };
            pathLabel.Font = new Font("Segoe UI Semibold", 8);
            pathLabel.Padding = new Padding(4, 7, 0, 0);
            pathLabel.ForeColor = GlassTheme.MutedText;
            files.Margin = new Padding(0, 0, 0, 1);
            files.Font = new Font("Segoe UI", 9.5f);
            files.ConfigureForDock(thumbnails);
            if (files.ContextMenuStrip != null) StylePopup(files.ContextMenuStrip);
            ((GlassButton)cancel).Quiet = true;
            ((GlassButton)cancel).CornerRadius = 7;
            cancel.Font = new Font("Segoe UI", 8);
            AccessibleName = "IPMsg Phone Dock";
            ResumeLayout(true);
        }

        private static void PaintNavigationBar(object sender, PaintEventArgs e)
        {
            Control control = (Control)sender;
            if (control.Width < 2 || control.Height < 2) return;
            GraphicsState saved = e.Graphics.Save();
            try
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                RectangleF bounds = new RectangleF(.5f, .5f, control.Width - 1, control.Height - 1);
                using (var path = GlassTheme.RoundRectangle(bounds, SystemInformation.HighContrast ? 0 : 12 * e.Graphics.DpiX / 96f))
                using (var fill = new SolidBrush(GlassTheme.Card))
                using (var edge = new Pen(SystemInformation.HighContrast ? SystemColors.WindowFrame : Color.FromArgb(22, Color.White)))
                { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(edge, path); }
            }
            finally { e.Graphics.Restore(saved); }
        }

        private static void StylePopup(ContextMenuStrip popup)
        {
            popup.BackColor = GlassTheme.Card; popup.ForeColor = GlassTheme.Text;
            popup.Font = new Font("Segoe UI", 9); popup.Padding = new Padding(4);
            popup.Renderer = new DockMenuRenderer();
        }
    }

    internal sealed class DockMenuRenderer : ToolStripProfessionalRenderer
    {
        public DockMenuRenderer() : base(new DockMenuColors()) { RoundedEdges = false; }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        { using (var edge = new Pen(GlassTheme.Divider)) e.Graphics.DrawRectangle(edge, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        { e.TextColor = e.Item.Enabled ? GlassTheme.Text : GlassTheme.MutedText; base.OnRenderItemText(e); }
    }
    internal sealed class DockMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return GlassTheme.Card; } }
        public override Color MenuItemSelected { get { return GlassTheme.Raised; } }
        public override Color MenuItemBorder { get { return GlassTheme.Divider; } }
        public override Color MenuItemSelectedGradientBegin { get { return GlassTheme.Raised; } }
        public override Color MenuItemSelectedGradientEnd { get { return GlassTheme.Raised; } }
        public override Color ImageMarginGradientBegin { get { return GlassTheme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return GlassTheme.Card; } }
        public override Color ImageMarginGradientEnd { get { return GlassTheme.Card; } }
        public override Color SeparatorDark { get { return GlassTheme.Divider; } }
        public override Color SeparatorLight { get { return GlassTheme.Card; } }
    }

    internal sealed class DockBrandMark : Control
    {
        public DockBrandMark()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent; TabStop = false; AccessibleName = "IPMsg";
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            float scale = e.Graphics.DpiX / 96f;
            float y = (Height - 17 * scale) / 2f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(GlassTheme.Accent, 1.3f * scale))
            using (var path = GlassTheme.RoundRectangle(new RectangleF(5 * scale, y, 10 * scale, 17 * scale), 3 * scale))
            {
                e.Graphics.DrawPath(pen, path);
                e.Graphics.DrawLine(pen, 8 * scale, y + 3 * scale, 12 * scale, y + 3 * scale);
                e.Graphics.DrawLine(pen, 9 * scale, y + 14 * scale, 11 * scale, y + 14 * scale);
            }
            using (var font = new Font("Segoe UI Semibold", 14f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(e.Graphics, "IPMsg", font, new Rectangle((int)(22 * scale), 0, Math.Max(1, Width - (int)(22 * scale)), Height), GlassTheme.Text,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // Label-compatible, so connection/restart code can keep setting Text and Visible normally.
    internal sealed class PhoneWelcome : Label
    {
        public PhoneWelcome()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent; AutoSize = false; AccessibleName = "Phone connection";
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            float scale = e.Graphics.DpiX / 96f;
            int center = Height / 2;
            bool welcome = String.IsNullOrEmpty(Text) || Text.StartsWith("Your phone", StringComparison.Ordinal);
            if (welcome)
            {
                float titleHeight;
                using (var titleFont = new Font("Segoe UI Semibold", 22 * 96f / 72f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                    titleHeight = TextRenderer.MeasureText(e.Graphics, "Your phone.\nRight here.", titleFont,
                        new Size(Math.Max(1, Width - (int)(24 * scale)), Int32.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                float titleTop = center - 25 * scale - titleHeight;
                if (Height > 430 * scale) DrawPhoneArt(e.Graphics, new PointF(Width / 2f, titleTop - 70 * scale), scale);
                DrawCentered(e.Graphics, "PHONE DOCK", 7.2f, false, titleTop - 21 * scale, 15 * scale, GlassTheme.MutedText);
                DrawCentered(e.Graphics, "Your phone.\nRight here.", 22, true, titleTop, titleHeight, GlassTheme.Text);
                DrawCentered(e.Graphics, "Control. Share. Keep your flow.", 8.5f, false, center - 17 * scale, 18 * scale, GlassTheme.MutedText);
                if (Height > 340 * scale)
                    DrawCentered(e.Graphics, "USB  /  LOCAL WI-FI", 7, false, center + 117 * scale, 18 * scale, GlassTheme.MutedText);
            }
            else
            {
                DrawPhoneArt(e.Graphics, new PointF(Width / 2f, center - 65 * scale), scale);
                string[] lines = (Text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                DrawCentered(e.Graphics, lines.Length == 0 ? "Opening your phone…" : lines[0], 13, true, center - 8 * scale, 30 * scale, GlassTheme.Text);
                if (lines.Length > 1) DrawCentered(e.Graphics, lines[1], 8.5f, false, center + 29 * scale, 45 * scale, GlassTheme.MutedText);
            }
        }
        internal static void DrawPhoneArt(Graphics graphics, PointF center, float scale)
        {
            GraphicsState state = graphics.Save();
            try
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TranslateTransform(center.X, center.Y); graphics.ScaleTransform(scale, scale);
                using (var tile = GlassTheme.RoundRectangle(new RectangleF(-37, -37, 74, 74), 22))
                using (var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(27, 30, 36)))
                using (var edge = new Pen(GlassTheme.Divider, .8f))
                { graphics.FillPath(fill, tile); graphics.DrawPath(edge, tile); }
                using (var phone = GlassTheme.RoundRectangle(new RectangleF(-14, -24, 28, 47), 6))
                using (var pen = new Pen(GlassTheme.Accent, 1.25f))
                using (var muted = new Pen(GlassTheme.MutedText, 1.1f))
                {
                    graphics.DrawPath(pen, phone);
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    graphics.DrawLine(pen, -4, -18, 4, -18);
                    graphics.DrawLine(muted, -3, 17, 3, 17);
                    using (var paper = GlassTheme.RoundRectangle(new RectangleF(4, -1, 22, 26), 4))
                    using (var fill = new SolidBrush(GlassTheme.Card))
                    { graphics.FillPath(fill, paper); graphics.DrawPath(muted, paper); }
                    graphics.DrawLine(pen, 10, 7, 20, 7); graphics.DrawLine(muted, 10, 12, 20, 12); graphics.DrawLine(muted, 10, 17, 16, 17);
                }
            }
            finally { graphics.Restore(state); }
        }
        private void DrawCentered(Graphics graphics, string text, float size, bool semibold, float top, float height, Color color)
        {
            float scale = graphics.DpiX / 96f;
            using (var font = new Font(semibold ? "Segoe UI Semibold" : "Segoe UI", size * 96f / 72f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                Rectangle bounds = new Rectangle((int)(12 * scale), (int)top, Math.Max(1, Width - (int)(24 * scale)), (int)height);
                TextRenderer.DrawText(graphics, text, font, bounds, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.PreserveGraphicsClipping);
            }
        }
    }
}
