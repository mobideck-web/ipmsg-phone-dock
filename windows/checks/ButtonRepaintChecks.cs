using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using IPMsg.Dock;

// Component-only raster regression checks. No Form, visible window, screenshot or phone.
internal static class ButtonRepaintChecks
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    private sealed class PaintHost : Panel
    {
        public Color Surface = Color.FromArgb(30, 36, 42);
        protected override void OnPaintBackground(PaintEventArgs e)
        { using (var fill = new SolidBrush(Surface)) e.Graphics.FillRectangle(fill, ClientRectangle); }
    }

    private sealed class ProbeButton : GlassButton
    {
        public bool OpaqueStyle { get { return GetStyle(ControlStyles.Opaque); } }
        public void Hover(bool value) { if (value) OnMouseEnter(EventArgs.Empty); else OnMouseLeave(EventArgs.Empty); }
        public void Render(Bitmap target)
        {
            using (Graphics graphics = Graphics.FromImage(target))
            using (var paint = new PaintEventArgs(graphics, ClientRectangle))
            {
                // Match Control's normal WM_PAINT background condition.
                if (!GetStyle(ControlStyles.Opaque)) OnPaintBackground(paint);
                OnPaint(paint);
            }
        }
    }

    [STAThread]
    private static int Main()
    {
        try
        {
            using (var parent = new PaintHost { Size = new Size(320, 100) })
            using (var button = new ProbeButton { Parent = parent, Location = new Point(12, 12), Size = new Size(112, 38),
                Font = new Font("Segoe UI", 10), Text = "⋯", BackColor = Color.Transparent })
            using (var canvas = NewBitmap(button.Size))
            {
                Require(!button.OpaqueStyle, "Owner-drawn translucent button clears inherited Opaque style");
                button.Render(canvas);
                byte[] normal = Pixels(canvas);
                for (int frame = 0; frame < 12; frame++) button.Render(canvas);
                Require(Equal(normal, Pixels(canvas)), "Repeated normal paints do not accumulate icon/text or translucent fill");

                button.Hover(true); button.Render(canvas);
                Require(!Equal(normal, Pixels(canvas)), "Hover changes the actual painted button");
                button.Hover(false); button.Render(canvas);
                Require(Equal(normal, Pixels(canvas)), "Leaving hover restores the original clean pixels");

                button.Text = "×"; button.Render(canvas);
                using (var fresh = NewBitmap(button.Size))
                {
                    button.Render(fresh);
                    Require(Equal(Pixels(fresh), Pixels(canvas)), "Changing the icon clears the previous glyph before drawing once");
                }

                parent.Surface = Color.FromArgb(65, 42, 29); button.Render(canvas);
                using (var fresh = NewBitmap(button.Size))
                {
                    button.Render(fresh);
                    Require(Equal(Pixels(fresh), Pixels(canvas)), "Parent-background changes repaint cleanly without old pixels");
                }
                Require((!parent.IsHandleCreated || !IsWindowVisible(parent.Handle)) &&
                    (!button.IsHandleCreated || !IsWindowVisible(button.Handle)) && Application.OpenForms.Count == 0,
                    "Component checks show no windows or application forms");
            }
            Console.WriteLine("RESULT: 7 component repaint checks passed. No visible windows, screenshots, app sessions or phone access.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); return 1; }
    }

    private static Bitmap NewBitmap(Size size)
    {
        var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.Magenta);
        return image;
    }
    private static byte[] Pixels(Bitmap image)
    {
        BitmapData data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] bytes = new byte[Math.Abs(data.Stride) * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { image.UnlockBits(data); }
    }
    private static bool Equal(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
        return true;
    }
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
