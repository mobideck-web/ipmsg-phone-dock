using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using IPMsg.Dock;

// Offline component raster fixtures. No DockForm, phone process, visible window, or screen capture.
internal static class PremiumPresentationChecks
{
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr handle);
    private sealed class FixtureSurface : Panel
    {
        public FixtureSurface() { BackColor = GlassTheme.Background; }
        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(GlassTheme.Background); }
    }

    [STAThread] private static int Main(string[] arguments)
    {
        try
        {
            string directory = arguments.Length == 0 ? Path.Combine(Path.GetTempPath(), "ipmsg-premium-fixture") : Path.GetFullPath(arguments[0]);
            Directory.CreateDirectory(directory);
            using (var surface = new FixtureSurface { Size = new Size(280, 720) })
            {
                Header(surface, 280, false);
                var welcome = new PhoneWelcome { Bounds = new Rectangle(6, 49, 268, 629), Text = "Your phone.\nRight here." };
                surface.Controls.Add(welcome);
                var connect = new GlassButton { Parent = welcome, Bounds = new Rectangle(48, 324, 172, 44), Text = "Connect", Primary = true,
                    CornerRadius = 11, Font = new Font("Segoe UI Semibold", 9.5f) };
                var wifi = new GlassButton { Parent = welcome, Bounds = new Rectangle(48, 376, 172, 34), Text = "Set up Wi-Fi", Quiet = true,
                    ForeColor = GlassTheme.MutedText, Font = new Font("Segoe UI", 9) };
                Footer(surface, 684, "Ready to connect");
                using (var bitmap = new Bitmap(surface.Width, surface.Height))
                {
                    surface.DrawToBitmap(bitmap, surface.ClientRectangle);
                    bitmap.Save(Path.Combine(directory, "welcome-280.png"), ImageFormat.Png);
                    string first = Digest(bitmap);
                    surface.DrawToBitmap(bitmap, surface.ClientRectangle);
                    Require(first == Digest(bitmap), "Complete welcome component repaint is stable");
                }
                RequireInvisible(surface);
                Require(connect.AccessibleRole == AccessibleRole.PushButton && connect.TabStop && wifi.TabStop, "Connection controls retain native keyboard and accessibility roles");
            }
            using (var surface = new FixtureSurface { Size = new Size(280, 376) })
            using (var previews = new ImageList { ImageSize = new Size(80, 80), ColorDepth = ColorDepth.Depth32Bit })
            using (var files = new SharedFileList { Size = new Size(268, 192), Font = new Font("Segoe UI", 9.5f) })
            {
                Header(surface, 280, true);
                Toolbar(surface);
                var label = new Label { Parent = surface, Bounds = new Rectangle(10, 102, 206, 24), Text = "3 items · temporary", ForeColor = GlassTheme.MutedText,
                    Font = new Font("Segoe UI Semibold", 8), Padding = new Padding(0, 6, 0, 0) };
                surface.Controls.Add(new DockIconButton { Bounds = new Rectangle(218, 103, 26, 26), Symbol = "clear", Quiet = true, AccessibleName = "Clear temporary files" });
                surface.Controls.Add(new DockIconButton { Bounds = new Rectangle(248, 103, 26, 26), Symbol = "close", Quiet = true, AccessibleName = "Hide files" });
                IntPtr previewHandle = previews.Handle;
                using (var photo = new Bitmap(80, 80))
                using (var graphics = Graphics.FromImage(photo))
                {
                    graphics.Clear(Color.FromArgb(91, 119, 128));
                    using (var sky = new SolidBrush(Color.FromArgb(191, 205, 207))) graphics.FillRectangle(sky, 0, 0, 80, 37);
                    using (var mountain = new SolidBrush(Color.FromArgb(68, 91, 95))) graphics.FillPolygon(mountain, new[] { new Point(0, 62), new Point(23, 25), new Point(46, 57), new Point(66, 32), new Point(80, 55), new Point(80, 80), new Point(0, 80) });
                    previews.Images.Add("photo", photo);
                }
                files.ConfigureForDock(previews);
                var photoRow = Row("photo", "Morning light.jpg", 4L * 1024 * 1024, "photo");
                var documentRow = Row("document", "Design notes — November.pdf", 1258291, "file");
                var waitingRow = Row("archive", "Photo collection.zip", 48234496, "file");
                ((BridgeItem)waitingRow.Tag).Mime = FolderSnapshot.Mime;
                files.Items.AddRange(new[] { photoRow, documentRow, waitingRow });
                files.CreateControl();
                files.RefreshMetrics(144);
                Require(files.Items[0].Bounds.Height >= 90, "Native ListView row metrics reserve the full 150% DPI card height");
                files.RefreshMetrics(96);
                files.SetPreparation("photo", 100, true, null);
                using (var nativeRows = new Bitmap(268, 192))
                {
                    files.DrawToBitmap(nativeRows, new Rectangle(0, 0, 268, 192));
                    nativeRows.Save(Path.Combine(directory, "native-file-list-280.png"), ImageFormat.Png);
                }
                files.SetPreparation("document", 64, false, null);
                files.SetPreparation("archive", 0, false, null);
                Require(files.IsPrepared("photo") && !files.IsPrepared("document"), "Readiness is explicit for each file");
                files.SetPreparation("photo", 100, true, "Source changed");
                Require(!files.IsPrepared("photo"), "A failed source cannot appear ready");
                files.SetPreparation("photo", 100, true, null);
                Require(files.View == View.Details && files.OwnerDraw && files.FullRowSelect && files.MultiSelect && files.HideSelection == false,
                    "File cards preserve native multi-selection and full-row keyboard navigation");
                Footer(surface, 340, "Preparing 1 of 3 · 64%");
                using (var bitmap = new Bitmap(280, 376))
                {
                    surface.DrawToBitmap(bitmap, surface.ClientRectangle);
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        files.DrawFileCard(graphics, new Rectangle(6, 134, 268, 62), photoRow, true, false, false);
                        files.DrawFileCard(graphics, new Rectangle(6, 196, 268, 62), documentRow, false, false, false);
                        files.DrawFileCard(graphics, new Rectangle(6, 258, 268, 62), waitingRow, false, false, true);
                    }
                    bitmap.Save(Path.Combine(directory, "shelf-280.png"), ImageFormat.Png);
                }
                using (var bitmap = new Bitmap(420, 288))
                {
                    bitmap.SetResolution(144, 144);
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(GlassTheme.Background);
                        files.DrawFileCard(graphics, new Rectangle(0, 0, 420, 93), photoRow, true, true, false);
                        files.DrawFileCard(graphics, new Rectangle(0, 93, 420, 93), documentRow, false, false, false);
                        files.DrawFileCard(graphics, new Rectangle(0, 186, 420, 93), waitingRow, false, false, false);
                    }
                    bitmap.Save(Path.Combine(directory, "shelf-150-percent.png"), ImageFormat.Png);
                }
                files.ClearPreparation(); Require(!files.IsPrepared("photo"), "Clearing the shelf removes stale readiness");
                RequireInvisible(surface); RequireInvisible(files);
            }
            using (var welcome = new PhoneWelcome { Size = new Size(268, 370), Text = "Your phone.\nRight here." })
            using (var bitmap = new Bitmap(268, 370))
            {
                welcome.BackColor = GlassTheme.Background;
                welcome.DrawToBitmap(bitmap, welcome.ClientRectangle);
                bitmap.Save(Path.Combine(directory, "welcome-compact-280.png"), ImageFormat.Png);
                RequireInvisible(welcome);
            }
            Require(Application.OpenForms.Count == 0, "Fixtures create no application forms or visible windows");
            Console.WriteLine("RESULT: 8 component checks passed. PNG fixtures saved to " + directory + ". No desktop capture or phone session.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Header(Control surface, int width, bool active)
    {
        surface.Controls.Add(new DockBrandMark { Bounds = new Rectangle(6, 3, active ? 78 : 142, 40) });
        string[] symbols = active ? new[] { "mute", "display", "layout", "more", "close" } : new[] { "layout", "more", "close" };
        int start = active ? 90 : 162;
        for (int index = 0; index < symbols.Length; index++)
            surface.Controls.Add(new DockIconButton { Bounds = new Rectangle(start + index * 36, 7, 32, 32), Symbol = symbols[index], Quiet = true, Active = symbols[index] == "display" });
    }
    private static void Toolbar(Control surface)
    {
        var bar = new GlassPanel { Parent = surface, Bounds = new Rectangle(6, 52, 268, 42), Tint = GlassTheme.Card, BorderVisible = true, CornerRadius = 12 };
        bar.Controls.Add(new DockIconButton { Bounds = new Rectangle(4, 6, 29, 30), Symbol = "back", Quiet = true });
        bar.Controls.Add(new DockIconButton { Bounds = new Rectangle(35, 6, 29, 30), Symbol = "home", Quiet = true });
        bar.Controls.Add(new GlassButton { Bounds = new Rectangle(66, 6, 161, 30), Text = "Files", IconName = "files", Quiet = true, Font = new Font("Segoe UI", 8.5f) });
        bar.Controls.Add(new DockIconButton { Bounds = new Rectangle(229, 6, 34, 30), Symbol = "camera", Quiet = true, AccessibleName = "Copy phone screenshot" });
    }
    private static void Footer(Control surface, int top, string text)
    { surface.Controls.Add(new Label { Bounds = new Rectangle(12, top, 256, 28), Text = text, ForeColor = GlassTheme.MutedText, Font = new Font("Segoe UI", 8), TextAlign = ContentAlignment.MiddleLeft }); }
    private static ListViewItem Row(string id, string name, long size, string image)
    { return new ListViewItem(name) { Name = id, Tag = new BridgeItem { Id = id, Name = name, Size = size }, ImageKey = image }; }
    private static void RequireInvisible(Control control)
    {
        if (control.IsHandleCreated && IsWindowVisible(control.Handle)) throw new InvalidOperationException("A component became visible.");
        foreach (Control child in control.Controls) RequireInvisible(child);
    }
    private static string Digest(Bitmap bitmap)
    { using (var stream = new MemoryStream()) { bitmap.Save(stream, ImageFormat.Png); using (var hash = System.Security.Cryptography.SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(stream.ToArray())); } }
    private static void Require(bool result, string message)
    { if (!result) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
