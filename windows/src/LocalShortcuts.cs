using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IPMsg.Dock
{
    // Only F6/F11/Escape in this dock are handled; no keystrokes are stored or forwarded here.
    internal sealed class LocalShortcuts : IDisposable
    {
        private delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
        private readonly Hook callback;
        private readonly Func<IntPtr> dock;
        private readonly Func<Keys, bool> handle;
        private IntPtr hook;
        private Keys held;
        public LocalShortcuts(Func<IntPtr> dock, Func<Keys, bool> handle)
        {
            this.dock = dock; this.handle = handle; callback = OnKey;
            hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        }
        private IntPtr OnKey(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && (message == (IntPtr)0x101 || message == (IntPtr)0x105))
            { if (Marshal.ReadInt32(data) == (int)held) held = Keys.None; }
            if (code >= 0 && (message == (IntPtr)0x100 || message == (IntPtr)0x104))
            {
                int key = Marshal.ReadInt32(data);
                if (key == (int)Keys.F6 || key == (int)Keys.F11 || key == (int)Keys.Escape)
                {
                    try
                    {
                        IntPtr foreground = GetForegroundWindow(), root = GetAncestor(foreground, 2);
                        if (foreground == dock() || root == dock())
                        { if (held == (Keys)key) return (IntPtr)1; if (handle((Keys)key)) { held = (Keys)key; return (IntPtr)1; } }
                    }
                    catch (InvalidOperationException) { }
                }
            }
            return CallNextHookEx(hook, code, message, data);
        }
        public void Dispose() { if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int kind, Hook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    }
    internal sealed class OverlayForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000; return p; } }
    }
}
