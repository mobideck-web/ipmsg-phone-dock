using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace IPMsg.Dock
{
    internal static class Native
    {
        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr child);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] internal static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
        internal static void BeginMove(IntPtr hwnd) { ReleaseCapture(); SendMessage(hwnd, 0xA1, (IntPtr)2, IntPtr.Zero); }
        [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        internal static IntPtr FindOwnedWindow(Process process, string title)
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                int id = process.Id;
                EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
                {
                    uint owner; GetWindowThreadProcessId(hwnd, out owner);
                    if (owner != id) return true;
                    var text = new StringBuilder(256); GetWindowText(hwnd, text, text.Capacity);
                    if (text.ToString() == title) { found = hwnd; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            catch (InvalidOperationException) { }
            return found;
        }

        internal static void Embed(IntPtr child, IntPtr parent)
        {
            // SDL retains input/rendering. All native file drops are routed through our copy-only ADB proxy.
            SetWindowLong(child, -16, (GetWindowLong(child, -16) & ~unchecked((int)0x80C40000)) | 0x40000000 | 0x10000000);
            SetParent(child, parent);
            if (GetParent(child) != parent) throw new IOException("Windows refused the embedded window. Run the dock and its runtime at the same normal user privilege level.");
            DragAcceptFiles(child, true);
            ShowWindow(child, 5);
        }

        internal static void DarkTitle(IntPtr hwnd)
        { try { int enabled = 1; DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)); } catch (DllNotFoundException) { } }
        internal static void GlassFrame(IntPtr hwnd)
        {
            DarkTitle(hwnd);
            if (System.Windows.Forms.SystemInformation.HighContrast) return;
            try
            {
                int backdrop = 2, rounded = 2;
                // Unsupported attributes return an HRESULT on Windows 10; the painted glass theme remains usable.
                DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));
                DwmSetWindowAttribute(hwnd, 33, ref rounded, sizeof(int));
            }
            catch (DllNotFoundException) { }
        }
        internal static void Park(IntPtr hwnd)
        {
            if (!IsWindow(hwnd)) return;
            ShowWindow(hwnd, 0);
            SetParent(hwnd, IntPtr.Zero);
            SetWindowLong(hwnd, -16, (GetWindowLong(hwnd, -16) & ~0x40000000) | unchecked((int)0x80000000));
        }
        internal static bool HasSize(IntPtr hwnd, int width, int height)
        { Rect rect; return GetClientRect(hwnd, out rect) && rect.Right - rect.Left == width && rect.Bottom - rect.Top == height; }
    }
}
