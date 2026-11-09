using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace IPMsg.Dock
{
    // Keep native ListView selection, keyboard navigation, tooltips and ItemDrag.
    // Only its presentation is custom: source metadata is never changed here.
    internal sealed class SharedFileList : ListView
    {
        private sealed class Preparation
        {
            public int Percent;
            public bool Ready;
            public string Error;
        }
        private readonly Dictionary<string, Preparation> states = new Dictionary<string, Preparation>(StringComparer.Ordinal);
        private readonly ImageList rowHeight = new ImageList();
        private int hoverIndex = -1;

        public SharedFileList()
        {
            DoubleBuffered = true;
            OwnerDraw = true;
            View = View.Details;
            HeaderStyle = ColumnHeaderStyle.None;
            FullRowSelect = true;
            HideSelection = false;
            MultiSelect = true;
            ShowItemToolTips = true;
            BorderStyle = BorderStyle.None;
            BackColor = GlassTheme.Background;
            ForeColor = GlassTheme.Text;
            Columns.Add("Shared file", 240);
            AccessibleName = "Shared phone files and folders";
            AccessibleDescription = "Use arrow keys to select items. Ready items can be dragged to your PC. Press Enter to save the selection.";
            SetRowHeight();
        }

        public void ConfigureForDock(ImageList previews)
        {
            View = View.Details;
            LargeImageList = previews;
            SmallImageList = rowHeight;
            HeaderStyle = ColumnHeaderStyle.None;
            FullRowSelect = true;
            LabelWrap = false;
            BackColor = GlassTheme.Background;
            FitColumn();
        }

        public void SetPreparation(string itemId, int percent, bool ready, string error)
        {
            if (String.IsNullOrEmpty(itemId) || IsDisposed) return;
            int value = Math.Max(0, Math.Min(100, percent));
            Preparation previous;
            bool changed = !states.TryGetValue(itemId, out previous) || previous.Percent != value || previous.Ready != ready || previous.Error != error;
            if (changed) states[itemId] = new Preparation { Percent = value, Ready = ready, Error = error };
            ListViewItem item = Items[itemId];
            if (item != null)
            {
                string detail = !String.IsNullOrEmpty(error) ? error : ready ? "Ready to drag to your PC" : "Preparing on this PC · " + Math.Max(0, Math.Min(100, percent)) + "%";
                item.ToolTipText = ItemName(item) + "\n" + detail;
                if (changed && IsHandleCreated) Invalidate(item.Bounds);
            }
        }

        public bool IsPrepared(string itemId)
        {
            Preparation state;
            return itemId != null && states.TryGetValue(itemId, out state) && state.Ready && String.IsNullOrEmpty(state.Error);
        }

        public void ClearPreparation()
        {
            states.Clear();
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (rowHeight != null) SetRowHeight();
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SetRowHeight(); FitColumn(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); FitColumn(); }
        protected override void OnDpiChangedAfterParent(EventArgs e)
        { base.OnDpiChangedAfterParent(e); SetRowHeight(); FitColumn(); Invalidate(); }
        private void SetRowHeight()
        { RefreshMetrics(DeviceDpi); }
        internal void RefreshMetrics(int dpi)
        {
            float scale = Math.Max(96, Math.Min(288, dpi)) / 96f;
            rowHeight.ImageSize = new Size(1, Math.Max(48, Math.Min(180, (int)Math.Round(60 * scale))));
            IntPtr handle = rowHeight.Handle; // Add must copy the spacer before its temporary Bitmap is disposed.
            if (rowHeight.Images.Count == 0)
                using (var spacer = new Bitmap(1, rowHeight.ImageSize.Height)) rowHeight.Images.Add(spacer);
            SmallImageList = rowHeight;
        }
        private void FitColumn()
        {
            if (Columns.Count > 0) Columns[0].Width = Math.Max(32, ClientSize.Width - 1);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            ListViewItem item = GetItemAt(e.X, e.Y);
            int next = item == null ? -1 : item.Index;
            if (next == hoverIndex) return;
            int previous = hoverIndex; hoverIndex = next;
            if (previous >= 0 && previous < Items.Count) Invalidate(Items[previous].Bounds);
            if (next >= 0) Invalidate(item.Bounds);
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverIndex = -1; Invalidate(); }
        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e) { e.DrawDefault = false; }
        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e) { e.DrawDefault = false; }
        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            Rectangle row = new Rectangle(0, e.Bounds.Top, ClientSize.Width, e.Bounds.Height);
            DrawFileCard(e.Graphics, row, e.Item, e.Item.Selected, e.Item.Focused && Focused && ShowFocusCues, hoverIndex == e.ItemIndex);
        }

        // Also used by the offline design fixture, without creating a window or connecting a phone.
        internal void DrawFileCard(Graphics graphics, Rectangle row, ListViewItem item, bool selected, bool focused, bool hovered)
        {
            float scale = graphics.DpiX / 96f;
            bool highContrast = SystemInformation.HighContrast;
            Rectangle card = new Rectangle(row.Left + Scale(2, scale), row.Top + Scale(2, scale), Math.Max(1, row.Width - Scale(4, scale)), Math.Max(1, row.Height - Scale(5, scale)));
            Color fill = highContrast ? selected ? SystemColors.Highlight : SystemColors.Window : selected ? GlassTheme.Raised : hovered ? Color.FromArgb(36, 40, 47) : GlassTheme.Card;
            Color text = highContrast && selected ? SystemColors.HighlightText : GlassTheme.Text;
            Color muted = highContrast && selected ? SystemColors.HighlightText : GlassTheme.MutedText;
            using (var background = new SolidBrush(BackColor)) graphics.FillRectangle(background, row);
            GraphicsState saved = graphics.Save();
            try
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = GlassTheme.RoundRectangle(card, highContrast ? 0 : 10 * scale))
                using (var background = new SolidBrush(fill))
                using (var edge = new Pen(highContrast ? selected ? SystemColors.HighlightText : SystemColors.WindowFrame : selected ? Color.FromArgb(130, GlassTheme.Accent) : Color.FromArgb(17, Color.White)))
                { graphics.FillPath(background, path); graphics.DrawPath(edge, path); }

                Rectangle thumbnail = new Rectangle(card.Left + Scale(8, scale), card.Top + Scale(8, scale), Scale(40, scale), Scale(40, scale));
                string name = ItemName(item);
                Image image = null;
                if (LargeImageList != null && item.ImageKey != "file" && LargeImageList.Images.ContainsKey(item.ImageKey)) image = LargeImageList.Images[item.ImageKey];
                if (image != null)
                {
                    using (image)
                    using (GraphicsPath clip = GlassTheme.RoundRectangle(thumbnail, 6 * scale))
                    {
                        graphics.SetClip(clip);
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(image, thumbnail);
                        graphics.Restore(saved); saved = graphics.Save(); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    }
                }
                else if (item.Tag is BridgeItem && FolderSnapshot.IsFolder((BridgeItem)item.Tag)) DrawFolder(graphics, thumbnail, scale, muted);
                else DrawDocument(graphics, thumbnail, name, scale, muted);

                int left = thumbnail.Right + Scale(10, scale);
                int right = card.Right - Scale(10, scale);
                var title = new Rectangle(left, card.Top + Scale(7, scale), Math.Max(1, right - left - Scale(12, scale)), Scale(17, scale));
                var metadata = new Rectangle(left, card.Top + Scale(25, scale), Math.Max(1, right - left), Scale(13, scale));
                var detail = new Rectangle(left, card.Top + Scale(40, scale), Math.Max(1, right - left), Scale(13, scale));
                Preparation state; states.TryGetValue(item.Name ?? "", out state);
                // Pixel fonts make GDI TextRenderer follow this surface's DPI explicitly.
                using (var titleFont = new Font("Segoe UI Semibold", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var smallFont = new Font("Segoe UI", 10.4f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    DrawText(graphics, name, titleFont, title, text);
                    bool folder = item.Tag is BridgeItem && FolderSnapshot.IsFolder((BridgeItem)item.Tag);
                    DrawText(graphics, (folder ? "Folder" : FileKind(name)) + "  ·  " + ItemSize(item), smallFont, metadata, muted);
                    bool failed = state != null && !String.IsNullOrEmpty(state.Error);
                    bool ready = state != null && state.Ready && !failed;
                    string message = failed ? "Couldn’t prepare · try again" : ready ? "Ready to drag" : state == null ? "Waiting to prepare" : "Preparing  ·  " + state.Percent + "%";
                    DrawText(graphics, message, smallFont, detail, failed ? GlassTheme.Danger : ready ? GlassTheme.Accent : muted);
                    if (ready) DrawGrip(graphics, new Point(right - Scale(3, scale), title.Top + Scale(5, scale)), scale, muted);
                    else if (!failed && state != null)
                    {
                        Rectangle track = new Rectangle(left, card.Bottom - Scale(3, scale), Math.Max(1, right - left), Math.Max(1, Scale(2, scale)));
                        using (var trackBrush = new SolidBrush(GlassTheme.Divider)) graphics.FillRectangle(trackBrush, track);
                        track.Width = (int)Math.Round(track.Width * state.Percent / 100.0);
                        if (track.Width > 0) using (var bar = new SolidBrush(GlassTheme.Accent)) graphics.FillRectangle(bar, track);
                    }
                }
                if (focused)
                {
                    Rectangle focus = Rectangle.Inflate(card, -Scale(3, scale), -Scale(3, scale));
                    ControlPaint.DrawFocusRectangle(graphics, focus, text, fill);
                }
            }
            finally { graphics.Restore(saved); }
        }

        private static void DrawDocument(Graphics graphics, Rectangle bounds, string name, float scale, Color color)
        {
            using (var tile = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(42, 47, 55)))
            using (var path = GlassTheme.RoundRectangle(bounds, 7 * scale)) graphics.FillPath(tile, path);
            RectangleF paper = new RectangleF(bounds.Left + 12 * scale, bounds.Top + 8 * scale, 17 * scale, 23 * scale);
            using (var pen = new Pen(color, 1.15f * scale))
            {
                pen.LineJoin = LineJoin.Round;
                graphics.DrawLines(pen, new[] { new PointF(paper.Left, paper.Bottom), new PointF(paper.Left, paper.Top), new PointF(paper.Right - 5 * scale, paper.Top), new PointF(paper.Right, paper.Top + 5 * scale), new PointF(paper.Right, paper.Bottom), new PointF(paper.Left, paper.Bottom) });
                graphics.DrawLines(pen, new[] { new PointF(paper.Right - 5 * scale, paper.Top), new PointF(paper.Right - 5 * scale, paper.Top + 5 * scale), new PointF(paper.Right, paper.Top + 5 * scale) });
                graphics.DrawLine(pen, paper.Left + 4 * scale, paper.Top + 12 * scale, paper.Right - 4 * scale, paper.Top + 12 * scale);
                graphics.DrawLine(pen, paper.Left + 4 * scale, paper.Top + 17 * scale, paper.Right - 6 * scale, paper.Top + 17 * scale);
            }
        }
        private static void DrawFolder(Graphics graphics, Rectangle bounds, float scale, Color color)
        {
            using (var tile = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(42, 47, 55)))
            using (var path = GlassTheme.RoundRectangle(bounds, 7 * scale)) graphics.FillPath(tile, path);
            float x = bounds.Left + 9 * scale, y = bounds.Top + 12 * scale;
            using (var pen = new Pen(color, 1.15f * scale))
            {
                pen.LineJoin = LineJoin.Round;
                graphics.DrawLines(pen, new[] { new PointF(x, y + 17 * scale), new PointF(x, y), new PointF(x + 8 * scale, y), new PointF(x + 11 * scale, y + 3 * scale), new PointF(x + 23 * scale, y + 3 * scale), new PointF(x + 23 * scale, y + 17 * scale), new PointF(x, y + 17 * scale) });
                graphics.DrawLine(pen, x, y + 6 * scale, x + 23 * scale, y + 6 * scale);
            }
        }
        private static void DrawGrip(Graphics graphics, Point point, float scale, Color color)
        {
            using (var brush = new SolidBrush(color))
                for (int x = 0; x < 2; x++) for (int y = 0; y < 3; y++) graphics.FillEllipse(brush, point.X + x * 3 * scale, point.Y + y * 3 * scale, 1.2f * scale, 1.2f * scale);
        }
        private static void DrawText(Graphics graphics, string text, Font font, Rectangle bounds, Color color)
        { TextRenderer.DrawText(graphics, text, font, bounds, color, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping); }
        private static int Scale(int value, float scale) { return (int)Math.Round(value * scale); }
        private static string ItemName(ListViewItem item)
        {
            BridgeItem file = item.Tag as BridgeItem;
            return file != null ? FolderSnapshot.DisplayName(file) : (item.Text ?? "").Split('\n')[0];
        }
        private static string ItemSize(ListViewItem item)
        {
            BridgeItem file = item.Tag as BridgeItem;
            if (file == null) { string[] lines = (item.Text ?? "").Split('\n'); return lines.Length > 1 ? lines[1] : "File"; }
            long bytes = Math.Max(0, file.Size);
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.#") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.#") + " GB";
        }
        private static string FileKind(string name)
        {
            string extension;
            try { extension = Path.GetExtension(name); } catch (ArgumentException) { extension = ""; }
            if (String.IsNullOrEmpty(extension)) return "File";
            string value = extension.TrimStart('.').ToUpperInvariant();
            if (value == "JPG" || value == "JPEG" || value == "PNG" || value == "WEBP" || value == "HEIC") return "Photo";
            if (value == "MP4" || value == "MOV" || value == "MKV") return "Video";
            return value.Length > 8 ? "File" : value;
        }
        protected override void Dispose(bool disposing) { if (disposing) rowHeight.Dispose(); base.Dispose(disposing); }
    }
}
