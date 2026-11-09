using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace IPMsg.Dock
{
    // Quiet native controls; the actual backdrop belongs to NativeGlass/DWM, not painted glows.
    public static class GlassTheme
    {
        public static Color Background { get { return SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(22, 24, 28); } }
        public static Color Card { get { return SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(31, 34, 40); } }
        public static Color Raised { get { return SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(40, 44, 51); } }
        public static Color Accent { get { return SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(193, 216, 238); } }
        public static Color Text { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(239, 241, 245); } }
        public static Color MutedText { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(156, 164, 177); } }
        public static Color Divider { get { return SystemInformation.HighContrast ? SystemColors.WindowFrame : Color.FromArgb(53, 58, 67); } }
        public static Color Danger { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(255, 160, 166); } }

        public static void Apply(Form form)
        {
            if (form == null) throw new ArgumentNullException("form");
            form.BackColor = Background;
            form.ForeColor = Text;
            NativeGlass.Apply(form);
            form.Invalidate(true);
        }

        public static void PaintBackdrop(Graphics graphics, Rectangle bounds)
        {
            if (graphics == null || bounds.Width <= 0 || bounds.Height <= 0) return;
            using (SolidBrush brush = new SolidBrush(Background)) graphics.FillRectangle(brush, bounds);
        }

        internal static GraphicsPath RoundRectangle(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(0, radius) * 2);
            if (diameter < 1)
            {
                path.AddRectangle(bounds);
                path.CloseFigure();
                return path;
            }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public class GlassSurface : Panel
    {
        private int cornerRadius = 16;
        private Color tint = Color.Empty;
        private bool borderVisible;

        public int CornerRadius
        {
            get { return cornerRadius; }
            set { cornerRadius = Math.Max(0, Math.Min(64, value)); Invalidate(); }
        }
        public Color Tint
        {
            get { return tint; }
            set { tint = value; Invalidate(); }
        }
        public bool BorderVisible
        {
            get { return borderVisible; }
            set { borderVisible = value; Invalidate(); }
        }

        public GlassSurface()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            ForeColor = GlassTheme.Text;
            Padding = new Padding(12);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (SystemInformation.HighContrast)
            {
                e.Graphics.Clear(SystemColors.Control);
                return;
            }
            base.OnPaintBackground(e);
            if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
            if (tint.IsEmpty && NativeGlass.IsActive(FindForm())) return;
            GraphicsState state = e.Graphics.Save();
            try
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                RectangleF bounds = new RectangleF(.5f, .5f, Width - 1, Height - 1);
                using (GraphicsPath path = GlassTheme.RoundRectangle(bounds, cornerRadius * e.Graphics.DpiX / 96f))
                using (SolidBrush fill = new SolidBrush(tint.IsEmpty ? GlassTheme.Card : tint))
                    e.Graphics.FillPath(fill, path);
            }
            finally { e.Graphics.Restore(state); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!borderVisible || ClientSize.Width < 2 || ClientSize.Height < 2) return;
            if (SystemInformation.HighContrast)
            {
                ControlPaint.DrawBorder(e.Graphics, ClientRectangle, SystemColors.WindowFrame, ButtonBorderStyle.Solid);
                return;
            }
            GraphicsState state = e.Graphics.Save();
            try
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = GlassTheme.RoundRectangle(new RectangleF(.5f, .5f, Width - 1, Height - 1),
                    cornerRadius * e.Graphics.DpiX / 96f))
                using (Pen border = new Pen(Color.FromArgb(45, Color.White)))
                    e.Graphics.DrawPath(border, path);
            }
            finally { e.Graphics.Restore(state); }
        }
    }

    public sealed class GlassPanel : GlassSurface { }

    public class GlassButton : Button
    {
        private bool hovered;
        private bool mousePressed;
        private bool keyboardPressed;
        private bool primary;
        private bool danger;
        private bool quiet;
        private string iconName;
        private int cornerRadius = 14;

        public bool Primary
        {
            get { return primary; }
            set { primary = value; Invalidate(); }
        }
        public bool Danger
        {
            get { return danger; }
            set { danger = value; Invalidate(); }
        }
        // Icon bars share one material surface; only the hovered item receives a fill.
        public bool Quiet
        {
            get { return quiet; }
            set { quiet = value; Invalidate(); }
        }
        public string IconName
        {
            get { return iconName; }
            set { iconName = value; Invalidate(); }
        }
        public int CornerRadius
        {
            get { return cornerRadius; }
            set { cornerRadius = Math.Max(0, Math.Min(40, value)); Invalidate(); }
        }

        public GlassButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Color.Transparent;
            ForeColor = GlassTheme.Text;
            Padding = new Padding(12, 0, 12, 0);
            Cursor = Cursors.Hand;
            AutoEllipsis = true;
            AccessibleRole = AccessibleRole.PushButton;
            // ButtonBase starts Opaque and normally repaints its entire background itself.
            // Our translucent owner drawing needs Control to repaint the parent every frame;
            // otherwise previous text/icons and hover fills remain under the next paint.
            SetStyle(ControlStyles.Opaque, false);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // This resolves to Control.OnPaintBackground, not Button.OnPaint: redraw the
            // transparent parent surface, then OnPaint below draws the label exactly once.
            base.OnPaintBackground(e);
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; base.OnMouseLeave(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) mousePressed = true; base.OnMouseDown(e); Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { mousePressed = false; base.OnMouseUp(e); Invalidate(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) mousePressed = false; base.OnMouseCaptureChanged(e); Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) keyboardPressed = true; base.OnKeyDown(e); Invalidate(); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) keyboardPressed = false; base.OnKeyUp(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { keyboardPressed = false; base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { mousePressed = false; keyboardPressed = false; base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnChangeUICues(UICuesEventArgs e) { base.OnChangeUICues(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Do not call Button.OnPaint: it draws its own text and would duplicate the custom label.
            if (Width < 2 || Height < 2) return;
            bool pressed = Enabled && (keyboardPressed || (mousePressed && hovered));
            bool highContrast = SystemInformation.HighContrast;
            Color text = Enabled ? ForeColor : GlassTheme.MutedText;
            Color fillColor;
            Color border = Color.Transparent;
            if (highContrast)
            {
                fillColor = pressed ? SystemColors.Highlight : SystemColors.Control;
                text = !Enabled ? SystemColors.GrayText : pressed ? SystemColors.HighlightText : SystemColors.ControlText;
                border = Focused ? SystemColors.Highlight : SystemColors.ControlText;
            }
            else if (!Enabled)
            {
                fillColor = quiet ? Color.Transparent : Color.FromArgb(9, Color.White);
                text = Color.FromArgb(108, 115, 127);
            }
            else if (primary)
            {
                fillColor = pressed ? Color.FromArgb(169, 192, 216) : hovered ? Color.FromArgb(218, 232, 247) : GlassTheme.Accent;
                text = Color.FromArgb(24, 37, 51);
                border = Color.FromArgb(32, Color.White);
            }
            else
            {
                fillColor = pressed ? Color.FromArgb(35, Color.White) : hovered ? Color.FromArgb(20, Color.White) : quiet ? Color.Transparent : Color.FromArgb(10, Color.White);
                if (!quiet) border = Color.FromArgb(17, Color.White);
                if (danger) text = GlassTheme.Danger;
            }
            GraphicsState state = e.Graphics.Save();
            try
            {
                e.Graphics.SmoothingMode = highContrast ? SmoothingMode.None : SmoothingMode.AntiAlias;
                RectangleF bounds = new RectangleF(.5f, .5f, Width - 1, Height - 1);
                using (GraphicsPath path = GlassTheme.RoundRectangle(bounds, highContrast ? 0 : cornerRadius * e.Graphics.DpiX / 96f))
                using (SolidBrush fill = new SolidBrush(fillColor))
                using (Pen outline = new Pen(border))
                {
                    e.Graphics.FillPath(fill, path);
                    if (highContrast || border.A > 0) e.Graphics.DrawPath(outline, path);
                }
                Rectangle label = new Rectangle(Padding.Left, Padding.Top,
                    Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
                if (pressed) label.Offset(0, 1);
                if (!String.IsNullOrEmpty(iconName))
                {
                    float scale = e.Graphics.DpiX / 96f;
                    int iconWidth = (int)Math.Round(20 * scale), gap = (int)Math.Round(7 * scale);
                    int textWidth = String.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(e.Graphics, Text, Font,
                        new Size(Int32.MaxValue, label.Height), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                    int groupWidth = Math.Min(label.Width, iconWidth + (textWidth == 0 ? 0 : gap + textWidth));
                    int groupLeft = label.Left + Math.Max(0, (label.Width - groupWidth) / 2);
                    int glyphSize = (int)Math.Round(32 * scale);
                    var icon = new Rectangle(groupLeft + (iconWidth - glyphSize) / 2, (Height - glyphSize) / 2, glyphSize, glyphSize);
                    if (pressed) icon.Offset(0, 1);
                    LucideIcons.Draw(e.Graphics, icon, iconName, text);
                    label.X = groupLeft + iconWidth + gap;
                    label.Width = Math.Max(0, groupWidth - iconWidth - gap);
                }
                else if (Image != null)
                {
                    int imageSize = Math.Min(label.Height, Image.Height);
                    Rectangle imageBounds = new Rectangle(String.IsNullOrEmpty(Text) ? (Width - imageSize) / 2 : label.Left,
                        (Height - imageSize) / 2, imageSize, imageSize);
                    if (Enabled) e.Graphics.DrawImage(Image, imageBounds);
                    else ControlPaint.DrawImageDisabled(e.Graphics, Image, imageBounds.Left, imageBounds.Top, highContrast ? SystemColors.Control : GlassTheme.Card);
                    if (!String.IsNullOrEmpty(Text))
                    {
                        label.X += imageSize + 7;
                        label.Width = Math.Max(0, label.Width - imageSize - 7);
                    }
                }
                TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;
                flags |= TextAlignment(TextAlign);
                if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
                else if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
                if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
                TextRenderer.DrawText(e.Graphics, Text, Font, label, text, flags);
                if (Focused && ShowFocusCues)
                {
                    Rectangle focus = Rectangle.Inflate(ClientRectangle, -4, -4);
                    if (focus.Width > 0 && focus.Height > 0)
                    {
                        if (highContrast) ControlPaint.DrawFocusRectangle(e.Graphics, focus, text, fillColor);
                        else using (GraphicsPath path = GlassTheme.RoundRectangle(focus, Math.Max(2, (cornerRadius - 3) * e.Graphics.DpiX / 96f)))
                        using (Pen ring = new Pen(primary ? Color.FromArgb(30, 55, 80) : GlassTheme.Accent, 1))
                        {
                            ring.DashStyle = DashStyle.Dot;
                            e.Graphics.DrawPath(ring, path);
                        }
                    }
                }
            }
            finally { e.Graphics.Restore(state); }
        }

        private static TextFormatFlags TextAlignment(ContentAlignment alignment)
        {
            TextFormatFlags flags = TextFormatFlags.SingleLine;
            if (alignment == ContentAlignment.TopCenter || alignment == ContentAlignment.MiddleCenter || alignment == ContentAlignment.BottomCenter)
                flags |= TextFormatFlags.HorizontalCenter;
            else if (alignment == ContentAlignment.TopRight || alignment == ContentAlignment.MiddleRight || alignment == ContentAlignment.BottomRight)
                flags |= TextFormatFlags.Right;
            if (alignment == ContentAlignment.MiddleLeft || alignment == ContentAlignment.MiddleCenter || alignment == ContentAlignment.MiddleRight)
                flags |= TextFormatFlags.VerticalCenter;
            else if (alignment == ContentAlignment.BottomLeft || alignment == ContentAlignment.BottomCenter || alignment == ContentAlignment.BottomRight)
                flags |= TextFormatFlags.Bottom;
            return flags;
        }
    }
}
