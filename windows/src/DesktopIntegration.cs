using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace IPMsg.Dock
{
    // Owns only this window's Shell reservation and hotkey. No global work-area replacement.
    public sealed class DesktopIntegration : NativeWindow, IDisposable
    {
        private readonly Form owner;
        private readonly Action activate, shortcutAction;
        private readonly Action<string> report;
        private readonly ShortcutRegistration shortcutRegistration;
        private ShortcutGesture shortcut;
        private readonly uint callbackMessage = RegisterWindowMessage("IPMsgDock.AppBar.Position.v1");
        private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        private bool reserve, suspended, applying, queued, disposed;
        private int requestedWidth;
        private IntPtr registeredHandle;
        private Rectangle reservedBounds;
        public bool IsReserved { get { return registeredHandle != IntPtr.Zero; } }
        public bool HotkeyRegistered { get { return shortcutRegistration.ActiveId != 0; } }
        public ShortcutGesture Shortcut { get { return shortcut; } }
        public string HotkeyError { get; private set; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct AppbarData
        {
            public uint Size; public IntPtr Window; public uint Callback; public uint Edge;
            public NativeRect Rectangle; public IntPtr Parameter;
        }
        [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref AppbarData data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr window, int id);

        public DesktopIntegration(Form owner, Action activate, Action<string> report)
            : this(owner, activate, activate, report, ShortcutGesture.Default) { }

        public DesktopIntegration(Form owner, Action activate, Action shortcutAction, Action<string> report, ShortcutGesture shortcut)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            this.owner = owner; this.activate = activate; this.shortcutAction = shortcutAction; this.report = report; this.shortcut = shortcut;
            shortcutRegistration = new ShortcutRegistration(
                delegate(int id, ShortcutGesture gesture) { return RegisterHotKey(Handle, id, gesture.Modifiers | 0x4000, (uint)gesture.Key); },
                delegate(int id) { UnregisterHotKey(Handle, id); });
            requestedWidth = owner.Width;
            owner.HandleCreated += OnHandleCreated;
            owner.HandleDestroyed += OnHandleDestroyed;
            owner.VisibleChanged += OnLayoutChanged;
            owner.Resize += OnLayoutChanged;
            owner.LocationChanged += OnLayoutChanged;
            owner.FormClosing += OnClosing;
            owner.Disposed += OnOwnerDisposed;
            Attach();
        }

        public void ReserveRight(bool enabled, int width, bool isSuspended)
        {
            if (disposed) return;
            reserve = enabled; requestedWidth = Math.Max(1, width); suspended = isSuspended;
            RefreshReservation();
        }

        public static bool ShouldReserve(bool requested, bool isSuspended, bool visible, bool minimized)
        { return requested && !isSuspended && visible && !minimized; }

        public static Rectangle RightEdgeBounds(Rectangle monitor, int width)
        {
            if (monitor.Width < 1 || monitor.Height < 1) throw new ArgumentException("Monitor bounds are unavailable.");
            int maximum = Math.Max(1, monitor.Width - Math.Min(240, monitor.Width / 2));
            int actual = Math.Max(1, Math.Min(width, maximum));
            return new Rectangle(monitor.Right - actual, monitor.Top, actual, monitor.Height);
        }

        private void Attach()
        {
            if (disposed || owner.IsDisposed || !owner.IsHandleCreated) return;
            if (Handle != owner.Handle) { if (Handle != IntPtr.Zero) ReleaseHandle(); AssignHandle(owner.Handle); }
            if (!HotkeyRegistered) { string error; if (!TrySetShortcut(shortcut, out error) && report != null) report(error); }
            RefreshReservation();
        }

        public bool TrySetShortcut(ShortcutGesture gesture, out string error)
        { return TrySetShortcut(gesture, null, out error); }
        public bool TrySetShortcut(ShortcutGesture gesture, Action persist, out string error)
        {
            error = null;
            if (disposed || !owner.IsHandleCreated || Handle == IntPtr.Zero) { error = "Open the dock before changing its shortcut."; return false; }
            if (!shortcutRegistration.TryReplace(gesture, persist))
            {
                error = gesture + " is already in use or unavailable (Windows error " + Marshal.GetLastWin32Error() + "). Choose another shortcut.";
                HotkeyError = error; return false;
            }
            shortcut = gesture; HotkeyError = null; return true;
        }

        private AppbarData Data(IntPtr window)
        { return new AppbarData { Size = (uint)Marshal.SizeOf(typeof(AppbarData)), Window = window, Callback = callbackMessage, Edge = 2 }; }

        private void RefreshReservation()
        {
            if (disposed || applying) return;
            bool eligible = owner.IsHandleCreated && ShouldReserve(reserve, suspended, owner.Visible, owner.WindowState == FormWindowState.Minimized);
            if (!eligible) { RemoveReservation(); return; }
            applying = true;
            try
            {
                if (registeredHandle == IntPtr.Zero)
                {
                    AppbarData add = Data(owner.Handle);
                    if (SHAppBarMessage(0, ref add) == UIntPtr.Zero)
                    { if (report != null) report("Windows could not reserve dock space. The dock remains a normal window."); return; }
                    registeredHandle = owner.Handle; reservedBounds = Rectangle.Empty;
                }
                Rectangle desired = RightEdgeBounds(Screen.FromHandle(owner.Handle).Bounds, requestedWidth);
                AppbarData data = Data(registeredHandle);
                data.Rectangle = new NativeRect { Left = desired.Left, Top = desired.Top, Right = desired.Right, Bottom = desired.Bottom };
                SHAppBarMessage(2, ref data); // ABM_QUERYPOS accounts for the taskbar/other AppBars.
                data.Rectangle.Left = data.Rectangle.Right - desired.Width;
                Rectangle candidate = Rectangle.FromLTRB(data.Rectangle.Left, data.Rectangle.Top, data.Rectangle.Right, data.Rectangle.Bottom);
                if (candidate.Width < 1 || candidate.Height < 1) throw new InvalidOperationException("No desktop area is available for the dock.");
                if (reservedBounds != candidate)
                {
                    SHAppBarMessage(3, ref data); // ABM_SETPOS may adjust the proposed rectangle again.
                    reservedBounds = Rectangle.FromLTRB(data.Rectangle.Left, data.Rectangle.Top, data.Rectangle.Right, data.Rectangle.Bottom);
                }
                if (owner.Bounds != reservedBounds) owner.Bounds = reservedBounds;
            }
            catch (Exception error)
            {
                RemoveReservation();
                if (report != null) report("Could not reserve dock space: " + error.Message);
            }
            finally { applying = false; }
        }

        private void RemoveReservation()
        {
            if (registeredHandle == IntPtr.Zero) return;
            IntPtr window = registeredHandle; registeredHandle = IntPtr.Zero; reservedBounds = Rectangle.Empty;
            AppbarData data = Data(window); SHAppBarMessage(1, ref data); // ABM_REMOVE restores Shell work area.
        }

        private void ScheduleRefresh()
        {
            if (disposed || applying || queued || owner.IsDisposed || !owner.IsHandleCreated) return;
            queued = true;
            try { owner.BeginInvoke((Action)delegate { queued = false; if (!disposed) RefreshReservation(); }); }
            catch (InvalidOperationException) { queued = false; }
        }

        protected override void WndProc(ref Message message)
        {
            int id = message.Msg;
            if (id == 0x312 && shortcutRegistration.ActiveId != 0 && message.WParam.ToInt32() == shortcutRegistration.ActiveId)
            { if (shortcutAction != null && !disposed) shortcutAction(); return; }
            if ((uint)id == SingleInstanceGate.ActivationMessage)
            {
                if (activate != null && !disposed) activate();
                return;
            }
            base.WndProc(ref message);
            if (disposed) return;
            if ((uint)id == taskbarCreated) { registeredHandle = IntPtr.Zero; reservedBounds = Rectangle.Empty; ScheduleRefresh(); }
            else if ((uint)id == callbackMessage && message.WParam.ToInt32() == 1 || id == 0x7E || id == 0x1A) ScheduleRefresh();
            else if (id == 0x6 && IsReserved)
            { AppbarData data = Data(registeredHandle); data.Parameter = (IntPtr)(message.WParam.ToInt64() & 0xFFFF); SHAppBarMessage(6, ref data); }
            else if (id == 0x47 && IsReserved && !applying)
            { AppbarData data = Data(registeredHandle); SHAppBarMessage(9, ref data); }
        }

        private void OnHandleCreated(object sender, EventArgs e) { Attach(); }
        private void OnHandleDestroyed(object sender, EventArgs e)
        {
            RemoveReservation();
            shortcutRegistration.Clear();
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }
        private void OnLayoutChanged(object sender, EventArgs e)
        {
            if (!owner.Visible || owner.WindowState == FormWindowState.Minimized) RemoveReservation();
            else ScheduleRefresh();
        }
        private void OnClosing(object sender, FormClosingEventArgs e) { if (!e.Cancel) RemoveReservation(); }
        private void OnOwnerDisposed(object sender, EventArgs e) { Dispose(); }

        // Called only from the explicit Create shortcut menu action. Never runs automatically.
        public static string CreateDesktopShortcut(string executablePath)
        {
            executablePath = Path.GetFullPath(executablePath);
            if (!File.Exists(executablePath)) throw new FileNotFoundException("The dock executable is missing.", executablePath);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string path = Path.Combine(desktop, "IPMsg Phone Dock.lnk");
            if (File.Exists(path)) throw new IOException("An IPMsg Phone Dock shortcut already exists on the desktop. Remove it before creating a replacement.");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("Windows shortcut support is unavailable.");
            object shell = null, shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type type = shortcut.GetType();
                SetCom(type, shortcut, "TargetPath", executablePath);
                SetCom(type, shortcut, "WorkingDirectory", Path.GetDirectoryName(executablePath));
                SetCom(type, shortcut, "Description", "Open IPMsg Phone Dock");
                SetCom(type, shortcut, "IconLocation", executablePath + ",0");
                // The running app owns the global shortcut. Assigning it to a .lnk as
                // well makes Explorer compete with RegisterHotKey after every launch.
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, new object[0]);
                return path;
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
        private static void SetCom(Type type, object target, string name, object value)
        { type.InvokeMember(name, BindingFlags.SetProperty, null, target, new object[] { value }); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; RemoveReservation();
            shortcutRegistration.Clear();
            owner.HandleCreated -= OnHandleCreated; owner.HandleDestroyed -= OnHandleDestroyed;
            owner.VisibleChanged -= OnLayoutChanged; owner.Resize -= OnLayoutChanged; owner.LocationChanged -= OnLayoutChanged;
            owner.FormClosing -= OnClosing; owner.Disposed -= OnOwnerDisposed;
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }
    }

    public sealed class ShortcutGesture
    {
        public uint Modifiers { get; private set; }
        public Keys Key { get; private set; }
        public static ShortcutGesture Default { get { return new ShortcutGesture(3, Keys.I); } }
        private ShortcutGesture(uint modifiers, Keys key) { Modifiers = modifiers; Key = key; }
        public static bool TryParse(string value, out ShortcutGesture gesture, out string error)
        {
            gesture = null; error = "Use modifiers and one key, for example Ctrl+Alt+I or Ctrl+Shift+F8.";
            if (String.IsNullOrWhiteSpace(value)) return false;
            string[] pieces = value.Trim().Split('+'); uint modifiers = 0; Keys key = Keys.None;
            foreach (string piece in pieces)
            {
                string part = piece.Trim(); uint flag = 0;
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) flag = 2;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) flag = 1;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) flag = 4;
                else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase)) flag = 8;
                if (flag != 0) { if ((modifiers & flag) != 0) return false; modifiers |= flag; continue; }
                if (key != Keys.None || part.Length == 0) return false;
                if (part.Length == 1 && part[0] >= '0' && part[0] <= '9') key = (Keys)((int)Keys.D0 + part[0] - '0');
                else if (!Enum.TryParse<Keys>(part, true, out key)) return false;
                bool supported = key >= Keys.A && key <= Keys.Z || key >= Keys.D0 && key <= Keys.D9 || key >= Keys.F1 && key <= Keys.F24 ||
                    key == Keys.Space || key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Insert || key == Keys.Delete ||
                    key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down;
                if (!supported || part.Length > 1 && Char.IsDigit(part[0])) return false;
            }
            if (key == Keys.None || (modifiers & 11) == 0) { error = "Include Ctrl, Alt or Win so normal typing is unaffected."; return false; }
            gesture = new ShortcutGesture(modifiers, key); error = null; return true;
        }
        public override string ToString()
        {
            string name = Key >= Keys.D0 && Key <= Keys.D9 ? ((int)Key - (int)Keys.D0).ToString() : Key.ToString();
            return ((Modifiers & 2) != 0 ? "Ctrl+" : "") + ((Modifiers & 1) != 0 ? "Alt+" : "") +
                ((Modifiers & 4) != 0 ? "Shift+" : "") + ((Modifiers & 8) != 0 ? "Win+" : "") + name;
        }
        public bool Matches(ShortcutGesture other) { return other != null && other.Modifiers == Modifiers && other.Key == Key; }
    }

    // Register the replacement before releasing the old binding. A conflict never
    // leaves the user without their working shortcut. The two ids are app-owned.
    internal sealed class ShortcutRegistration
    {
        private readonly Func<int, ShortcutGesture, bool> register;
        private readonly Action<int> unregister;
        private ShortcutGesture current;
        internal int ActiveId { get; private set; }
        internal ShortcutRegistration(Func<int, ShortcutGesture, bool> register, Action<int> unregister)
        { this.register = register; this.unregister = unregister; }
        internal bool TryReplace(ShortcutGesture wanted)
        { return TryReplace(wanted, null); }
        internal bool TryReplace(ShortcutGesture wanted, Action persist)
        {
            if (wanted == null) { if (persist != null) persist(); Clear(); return true; }
            if (ActiveId != 0 && wanted.Matches(current)) { if (persist != null) persist(); return true; }
            int next = ActiveId == 0x4950 ? 0x4951 : 0x4950;
            if (!register(next, wanted)) return false;
            try { if (persist != null) persist(); }
            catch { unregister(next); throw; }
            int previous = ActiveId; ActiveId = next; current = wanted;
            if (previous != 0) unregister(previous);
            return true;
        }
        internal void Clear() { int previous = ActiveId; ActiveId = 0; current = null; if (previous != 0) unregister(previous); }
    }

    public sealed class SingleInstanceGate : IDisposable
    {
        private readonly Mutex mutex;
        public bool IsOwner { get; private set; }
        public static readonly uint ActivationMessage = RegisterWindowMessage("IPMsg.PhoneDock.Activate.v1." + CurrentUser());
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        private static string CurrentUser() { using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) return identity.User == null ? Environment.UserName : identity.User.Value; }
        public SingleInstanceGate()
        {
            bool created;
            mutex = new Mutex(true, @"Local\IPMsg.PhoneDock." + CurrentUser(), out created);
            IsOwner = created;
        }
        public void ActivateExisting()
        {
            for (int attempt = 0; attempt < 4; attempt++)
            { PostMessage((IntPtr)0xFFFF, ActivationMessage, IntPtr.Zero, IntPtr.Zero); if (attempt < 3) Thread.Sleep(150); }
        }
        public void Dispose()
        {
            if (IsOwner) { try { mutex.ReleaseMutex(); } catch (ApplicationException) { } IsOwner = false; }
            mutex.Dispose();
        }
    }
}
