using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IPMsg.Dock;

// Pure geometry/lifecycle eligibility + generated PNG fixtures. No Shell reservation/hotkey,
// actual shortcut, clipboard write, screenshot API, ADB command or visible app is invoked.
internal static class DesktopFeatureChecks
{
    private static int count;
    [STAThread]
    private static int Main()
    {
        try
        {
            Check(DesktopIntegration.ShouldReserve(true, false, true, false), "Visible enabled dock is eligible for its AppBar reservation");
            Check(!DesktopIntegration.ShouldReserve(false, false, true, false), "Disabled reservation leaves the work area untouched");
            Check(!DesktopIntegration.ShouldReserve(true, true, true, false), "Fullscreen/collapse suspension releases eligibility");
            Check(!DesktopIntegration.ShouldReserve(true, false, false, false), "Hidden windows are not eligible");
            Check(!DesktopIntegration.ShouldReserve(true, false, true, true), "Minimized windows are not eligible");
            Rectangle edge = DesktopIntegration.RightEdgeBounds(new Rectangle(-1920, 0, 1920, 1080), 380);
            Check(edge == new Rectangle(-380, 0, 380, 1080), "Right-edge geometry supports a monitor with negative coordinates");
            Rectangle bounded = DesktopIntegration.RightEdgeBounds(new Rectangle(100, 200, 800, 600), 10000);
            Check(bounded.Left >= 100 && bounded.Right == 900 && bounded.Height == 600, "Oversized reservation is clamped within its selected monitor");
            Expect<ArgumentException>(delegate { DesktopIntegration.RightEdgeBounds(Rectangle.Empty, 380); }, "Unavailable monitor geometry is rejected");

            ShortcutGesture gesture; string shortcutError;
            Check(ShortcutGesture.TryParse(" alt + control + i ", out gesture, out shortcutError) && gesture.ToString() == "Ctrl+Alt+I" && gesture.Matches(ShortcutGesture.Default), "Personal shortcut parsing normalizes modifier order and casing");
            Check(ShortcutGesture.TryParse("Win+Shift+F24", out gesture, out shortcutError) && gesture.Modifiers == 12 && gesture.Key == Keys.F24, "Personal shortcut parsing accepts supported function keys and Windows modifier");
            Check(ShortcutGesture.TryParse("Ctrl+5", out gesture, out shortcutError) && gesture.ToString() == "Ctrl+5", "Digit shortcuts use the correct virtual key");
            foreach (string invalid in new[] { "I", "Shift+I", "Ctrl+Ctrl+I", "Ctrl+Alt", "Ctrl+Alt+I+J", "Ctrl+73", "Ctrl+Alt+0x49", "Ctrl+LControlKey" })
                Check(!ShortcutGesture.TryParse(invalid, out gesture, out shortcutError), "Malformed or typing-interfering shortcut rejected: " + invalid);
            var registrations = new List<int>(); var released = new List<int>(); bool reject = false;
            var registration = new ShortcutRegistration(delegate(int id, ShortcutGesture requested) { if (reject) return false; registrations.Add(id); return true; }, delegate(int id) { released.Add(id); });
            Check(registration.TryReplace(ShortcutGesture.Default), "Initial global shortcut registers through the owned registration boundary");
            int original = registration.ActiveId; ShortcutGesture alternate;
            ShortcutGesture.TryParse("Ctrl+Shift+F8", out alternate, out shortcutError); reject = true;
            Check(!registration.TryReplace(alternate) && registration.ActiveId == original && released.Count == 0, "Conflicting replacement keeps the previous global shortcut active");
            reject = false;
            Check(registration.TryReplace(alternate) && registration.ActiveId != original && released.Count == 1 && released[0] == original && registrations.Count == 2, "Successful replacement registers a different id before releasing the previous binding");
            Check(registration.TryReplace(alternate) && registrations.Count == 2 && released.Count == 1, "Unchanged shortcut does not register a duplicate binding");
            Check(registration.TryReplace(null) && registration.ActiveId == 0 && released.Count == 2, "Disabling the shortcut unregisters the owned active binding");
            registration.Clear(); Check(released.Count == 2, "Repeated shortcut cleanup is harmless");
            var transactionalReleased = new List<int>();
            var transactional = new ShortcutRegistration(delegate(int id, ShortcutGesture requested) { return true; }, delegate(int id) { transactionalReleased.Add(id); });
            transactional.TryReplace(ShortcutGesture.Default); int retained = transactional.ActiveId;
            Expect<IOException>(delegate { transactional.TryReplace(alternate, delegate { throw new IOException("Fixture settings save failed."); }); }, "A preference write failure propagates for the settings dialog");
            Check(transactional.ActiveId == retained && transactionalReleased.Count == 1 && transactionalReleased[0] != retained, "Failed preference save unregisters only the candidate and preserves the previous shortcut");
            Expect<IOException>(delegate { transactional.TryReplace(null, delegate { throw new IOException("Fixture disable save failed."); }); }, "A failed disable preference save is reported");
            Check(transactional.ActiveId == retained && transactionalReleased.Count == 1, "Failed disable save preserves the enabled shortcut");
            bool previousHeldDuringSave = false;
            Check(transactional.TryReplace(alternate, delegate { previousHeldDuringSave = transactional.ActiveId == retained && !transactionalReleased.Contains(retained); }) && previousHeldDuringSave && transactionalReleased.Contains(retained), "Previous shortcut remains registered throughout successful settings persistence");
            transactional.Clear();

            byte[] png;
            using (var fixture = new Bitmap(11, 7, PixelFormat.Format32bppArgb))
            using (var stream = new MemoryStream())
            {
                using (Graphics graphics = Graphics.FromImage(fixture)) graphics.Clear(Color.Teal);
                fixture.SetPixel(3, 2, Color.Magenta); fixture.Save(stream, ImageFormat.Png); png = stream.ToArray();
            }
            using (Bitmap decoded = PhoneScreenshot.DecodePng(png))
                Check(decoded.Width == 11 && decoded.Height == 7 && decoded.GetPixel(3, 2).ToArgb() == Color.Magenta.ToArgb(), "Generated phone-format PNG decodes with its dimensions and pixel content intact");
            byte[] bad = (byte[])png.Clone(); bad[0] = 0;
            Expect<IOException>(delegate { using (var image = PhoneScreenshot.DecodePng(bad)) { } }, "Non-PNG output is rejected before decoding");
            byte[] shortPng = new byte[png.Length - 12]; Array.Copy(png, shortPng, shortPng.Length);
            Expect<IOException>(delegate { using (var image = PhoneScreenshot.DecodePng(shortPng)) { } }, "A missing PNG completion marker is rejected");
            byte[] huge = (byte[])png.Clone(); Write32(huge, 16, 10000); Write32(huge, 20, 10000);
            Expect<IOException>(delegate { using (var image = PhoneScreenshot.DecodePng(huge)) { } }, "Decompression-bomb dimensions are rejected before image allocation");
            using (var form = new Form())
            {
                NativeGlass.SetAllowed(form, false);
                Check(!NativeGlass.GetAllowed(form) && !NativeGlass.IsActive(form) && !form.IsHandleCreated, "Per-window transparency can be disabled without creating a window");
                NativeGlass.SetAllowed(form, true);
                Check(NativeGlass.GetAllowed(form) && !form.IsHandleCreated, "Transparency preference can be restored without creating a window");
            }
            using (Bitmap image = PhoneScreenshot.DecodePng(png))
                Expect<InvalidOperationException>(delegate { Task.Run(delegate { PhoneScreenshot.SetClipboardAsync(image, CancellationToken.None).GetAwaiter().GetResult(); }).GetAwaiter().GetResult(); }, "Screenshot clipboard helper rejects a worker thread before touching the clipboard");
            Console.WriteLine("RESULT: " + count + " desktop feature fixture checks passed. No real AppBar, hotkey, shortcut, clipboard write, phone capture or visible window.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }
    private static void Write32(byte[] bytes, int offset, uint value)
    { bytes[offset] = (byte)(value >> 24); bytes[offset + 1] = (byte)(value >> 16); bytes[offset + 2] = (byte)(value >> 8); bytes[offset + 3] = (byte)value; }
    private static void Check(bool pass, string message)
    { if (!pass) throw new Exception(message); count++; Console.WriteLine("PASS: " + message); }
    private static void Expect<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { Check(true, message); return; } throw new Exception(message); }
}
