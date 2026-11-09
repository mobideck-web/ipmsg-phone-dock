using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IPMsg.Dock
{
    // Application-scoped native backdrop only. No layered Form opacity, colour-key transparency,
    // desktop capture, global Windows settings changes, or translucent SDL/video surfaces.
    public static class NativeGlass
    {
        private static readonly ConditionalWeakTable<Form, BackdropWindow> windows = new ConditionalWeakTable<Form, BackdropWindow>();
        private static readonly SetCompositionAttribute setComposition = FindCompositionFunction();
        public static string Mode { get; private set; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins { public int Left, Right, Top, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy { public int State, Flags, GradientColor, Animation; }
        [StructLayout(LayoutKind.Sequential)]
        private struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool SetCompositionAttribute(IntPtr window, ref CompositionData data);

        [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);

        static NativeGlass() { Mode = "Solid"; }

        public static bool Apply(Form form)
        {
            if (form == null) throw new ArgumentNullException("form");
            if (form.IsDisposed) return false;
            BackdropWindow window = windows.GetValue(form, delegate(Form owner) { return new BackdropWindow(owner); });
            window.Refresh();
            return window.Active;
        }

        public static void SetAllowed(Form form, bool allowed)
        {
            if (form == null) throw new ArgumentNullException("form");
            if (form.IsDisposed) return;
            BackdropWindow window = windows.GetValue(form, delegate(Form owner) { return new BackdropWindow(owner); });
            window.Allowed = allowed;
            window.Refresh();
        }

        public static bool GetAllowed(Form form)
        {
            BackdropWindow state;
            return form == null || !windows.TryGetValue(form, out state) || state.Allowed;
        }

        public static bool IsActive(Form form)
        {
            BackdropWindow state;
            return form != null && windows.TryGetValue(form, out state) && state.Active;
        }

        public static string GetMode(Form form)
        {
            BackdropWindow state;
            return form != null && windows.TryGetValue(form, out state) ? state.ModeName : "Solid";
        }

        public static void PaintBackground(Form form, PaintEventArgs e)
        {
            if (!IsActive(form)) { GlassTheme.PaintBackdrop(e.Graphics, form.ClientRectangle); return; }
            // Microsoft DWM custom-frame guidance: black GDI pixels have alpha0 in the extended
            // frame, exposing the system-drawn material. Opaque gradients would cover it up.
            IntPtr dc = e.Graphics.GetHdc();
            try
            {
                Rect rect = new Rect { Left = e.ClipRectangle.Left, Top = e.ClipRectangle.Top,
                    Right = e.ClipRectangle.Right, Bottom = e.ClipRectangle.Bottom };
                FillRect(dc, ref rect, GetStockObject(4)); // BLACK_BRUSH, borrowed stock object.
            }
            finally { e.Graphics.ReleaseHdc(dc); }
        }

        private static bool EffectsAllowed()
        {
            if (SystemInformation.HighContrast || SystemInformation.TerminalServerSession) return false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object enabled = key == null ? null : key.GetValue("EnableTransparency");
                    if (enabled != null && Convert.ToInt32(enabled) == 0) return false;
                }
            }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (FormatException) { return false; }
            return true;
        }

        private static SetCompositionAttribute FindCompositionFunction()
        {
            IntPtr module = GetModuleHandle("user32.dll");
            IntPtr address = module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module, "SetWindowCompositionAttribute");
            return address == IntPtr.Zero ? null : (SetCompositionAttribute)Marshal.GetDelegateForFunctionPointer(address, typeof(SetCompositionAttribute));
        }

        private static bool Accent(IntPtr window, bool enabled)
        {
            if (setComposition == null) return false;
            // Win10 compatibility path. The function is exported, but WCA_ACCENT_POLICY / state4
            // are not a supported public Acrylic contract; guard every use and retain Solid fallback.
            AccentPolicy accent = new AccentPolicy { State = enabled ? 4 : 0, Flags = enabled ? 2 : 0,
                GradientColor = enabled ? unchecked((int)0xB3202020) : 0 };
            IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AccentPolicy)));
            try
            {
                Marshal.StructureToPtr(accent, data, false);
                CompositionData setting = new CompositionData { Attribute = 19, Data = data, Size = Marshal.SizeOf(typeof(AccentPolicy)) };
                return setComposition(window, ref setting);
            }
            catch (EntryPointNotFoundException) { return false; }
            finally { Marshal.FreeHGlobal(data); }
        }

        private sealed class BackdropWindow : NativeWindow
        {
            private readonly Form form;
            private bool applying, moving;
            public bool Active { get; private set; }
            public bool Allowed { get; set; }
            public string ModeName { get; private set; }

            public BackdropWindow(Form form)
            {
                this.form = form; ModeName = "Solid"; Allowed = true;
                form.HandleCreated += delegate { Attach(); Refresh(); };
                form.HandleDestroyed += delegate { Active = false; ModeName = "Solid"; if (Handle != IntPtr.Zero) ReleaseHandle(); };
                form.Disposed += delegate { if (Handle != IntPtr.Zero) ReleaseHandle(); };
                Attach();
            }

            private void Attach()
            {
                if (form.IsHandleCreated && !form.IsDisposed && Handle != form.Handle)
                {
                    if (Handle != IntPtr.Zero) ReleaseHandle();
                    AssignHandle(form.Handle);
                }
            }

            public void Refresh()
            {
                if (applying || form.IsDisposed || !form.IsHandleCreated) return;
                applying = true;
                bool active = false;
                string mode = "Solid";
                try
                {
                    Attach();
                    bool composition;
                    bool allowed = Allowed && EffectsAllowed() && DwmIsCompositionEnabled(out composition) >= 0 && composition;
                    int dark = SystemInformation.HighContrast ? 0 : 1;
                    DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
                    int build = Environment.OSVersion.Version.Build;
                    if (allowed && build >= 22621)
                    {
                        int acrylic = 3; // DWMSBT_TRANSIENTWINDOW: documented Desktop Acrylic.
                        active = DwmSetWindowAttribute(Handle, 38, ref acrylic, sizeof(int)) >= 0;
                        if (active) mode = "Acrylic";
                    }
                    else if (allowed && build >= 17134 && build < 22000 && !moving)
                    {
                        active = Accent(Handle, true);
                        if (active) mode = "Acrylic";
                    }
                    if (!active)
                    {
                        int none = 1;
                        DwmSetWindowAttribute(Handle, 38, ref none, sizeof(int));
                        if (build < 22000) Accent(Handle, false);
                    }
                    Margins margins = active ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 } : new Margins();
                    if (DwmExtendFrameIntoClientArea(Handle, ref margins) < 0) { active = false; mode = "Solid"; }
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
                finally
                {
                    bool changed = Active != active;
                    Active = active; ModeName = mode; NativeGlass.Mode = mode;
                    form.BackColor = active ? Color.Black : GlassTheme.Background;
                    applying = false;
                    if (changed) form.Invalidate(true);
                }
            }

            protected override void WndProc(ref Message message)
            {
                int id = message.Msg;
                base.WndProc(ref message);
                if (id == 0x231) // WM_ENTERSIZEMOVE: avoid Win10 compatibility-acrylic drag stalls.
                { moving = true; if (Environment.OSVersion.Version.Build < 22000) Refresh(); }
                else if (id == 0x232) { moving = false; Refresh(); }
                else if (id == 0x31E || id == 0x31A || id == 0x1A) Refresh(); // Composition/theme/settings.
            }
        }
    }
}
