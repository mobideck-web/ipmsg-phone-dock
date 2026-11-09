using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace IPMsg.Dock.Setup
{
    internal sealed class SetupOperation : IDisposable
    {
        private readonly Mutex mutex;
        private bool owned;
        internal SetupOperation()
        {
            string identity;
            using (WindowsIdentity user = WindowsIdentity.GetCurrent()) identity = user.User.Value;
            mutex = new Mutex(false, "Global\\IPMsgDock.Setup." + identity);
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) { mutex.Dispose(); throw new IOException("Another IPMsg Dock install or uninstall is already running. Close it, then try again."); }
        }
        public void Dispose()
        {
            if (owned) { mutex.ReleaseMutex(); owned = false; }
            mutex.Dispose();
        }
    }

    internal sealed class InstallOptions
    {
        internal bool Desktop = true;
        internal bool StartAtSignIn = false;
    }

    internal sealed class ShortcutChange
    {
        internal string Path;
        internal byte[] Before;
        internal byte[] After;
        internal bool Applied;
    }

    internal sealed class RegistryValueBackup
    {
        internal string Key;
        internal string Name;
        internal bool Existed;
        internal object Value;
        internal RegistryValueKind Kind;
    }

    internal sealed class WindowsIntegration
    {
        internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\IPMsgDock";
        internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal static string InstallRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "IPMsgDock"); } }
        internal static string StartMenuShortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "IPMsg Dock.lnk"); } }
        internal static string DesktopShortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "IPMsg Dock.lnk"); } }
        internal static string StartupCommand(string root) { return "\"" + Path.Combine(root, "IPMsgDock.exe") + "\" --background"; }

        private readonly string root;
        private readonly InstallOptions options;
        private readonly List<ShortcutChange> changes = new List<ShortcutChange>();
        private readonly List<RegistryValueBackup> registry = new List<RegistryValueBackup>();
        private bool registryTouched;

        internal WindowsIntegration(string installRoot, InstallOptions installOptions) { root = SafePaths.Root(installRoot); options = installOptions; }

        internal void Prepare(Receipt previous, string stage)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                if (key != null && (!string.Equals(Convert.ToString(key.GetValue("InstallLocation"), CultureInfo.InvariantCulture), root, StringComparison.OrdinalIgnoreCase) || key.GetSubKeyNames().Length != 0))
                    throw new IOException("Another installation owns the IPMsg Dock uninstall entry. Setup has not changed it.");
            }
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                object value = key == null ? null : key.GetValue("IPMsgDock");
                if (options.StartAtSignIn && value != null && !string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), StartupCommand(root), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Another command already uses the IPMsgDock sign-in entry. Setup has not changed it.");
            }
            PrepareShortcut(StartMenuShortcut, true, previous, stage, "start");
            PrepareShortcut(DesktopShortcut, options.Desktop, previous, stage, "desktop");
            foreach (string name in ManagedUninstallNames) BackupValue(UninstallKey, name);
            BackupValue(RunKey, "IPMsgDock");
        }

        private void PrepareShortcut(string path, bool wanted, Receipt previous, string stage, string suffix)
        {
            SafePaths.NoReparse(path);
            OwnedFile old = previous.Shortcuts.FirstOrDefault(delegate(OwnedFile f) { return f.Path.Equals(path, StringComparison.OrdinalIgnoreCase); });
            bool exists = File.Exists(path);
            if (exists && (old == null || !FileProof.Matches(path, old)))
            {
                if (!wanted) return; // A changed or unrelated shortcut belongs to the user.
                throw new IOException("An existing shortcut was preserved. Move or rename it before installing: " + path);
            }
            byte[] after = null;
            if (wanted)
            {
                string temporary = SafePaths.Under(stage, ".shortcut-" + suffix + ".lnk");
                CreateShortcut(temporary, Path.Combine(root, "IPMsgDock.exe"), root);
                after = File.ReadAllBytes(temporary);
                File.Delete(temporary);
            }
            if (exists || wanted) changes.Add(new ShortcutChange { Path = path, Before = exists ? File.ReadAllBytes(path) : null, After = after });
        }

        internal void Apply(Receipt previous, Receipt receipt)
        {
            foreach (ShortcutChange change in changes)
            {
                SafePaths.NoReparse(change.Path);
                byte[] current = File.Exists(change.Path) ? File.ReadAllBytes(change.Path) : null;
                if (!Equal(current, change.Before)) throw new IOException("A shortcut changed during setup: " + change.Path);
                if (change.After == null) { File.Delete(change.Path); change.Applied = true; }
                else
                {
                    WriteShortcut(change.Path, change.After);
                    change.Applied = true;
                    receipt.Shortcuts.Add(FileProof.Describe(change.Path, change.Path));
                }
            }
            registryTouched = true;
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "IPMsg Dock");
                key.SetValue("DisplayVersion", BuildInfo.Version);
                key.SetValue("Publisher", "IPMsg contributors");
                key.SetValue("InstallLocation", root);
                key.SetValue("DisplayIcon", Path.Combine(root, "IPMsgDock.exe") + ",0");
                key.SetValue("UninstallString", "\"" + Path.Combine(root, "Uninstall.exe") + "\"");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long size = receipt.Files.Sum(delegate(OwnedFile f) { return f.Length; }) / 1024;
                key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, size), RegistryValueKind.DWord);
                key.SetValue("InstallDate", DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (options.StartAtSignIn) key.SetValue("IPMsgDock", StartupCommand(root));
                else if (string.Equals(Convert.ToString(key.GetValue("IPMsgDock"), CultureInfo.InvariantCulture), StartupCommand(root), StringComparison.OrdinalIgnoreCase))
                    key.DeleteValue("IPMsgDock", false);
            }
        }

        internal void Undo()
        {
            if (registryTouched)
            {
                foreach (RegistryValueBackup backup in registry)
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(backup.Key))
                    {
                        if (backup.Existed) key.SetValue(backup.Name, backup.Value, backup.Kind);
                        else key.DeleteValue(backup.Name, false);
                    }
                }
                RemoveEmptyRegistryKey(UninstallKey);
            }
            foreach (ShortcutChange change in changes.AsEnumerable().Reverse())
            {
                if (!change.Applied) continue;
                SafePaths.NoReparse(change.Path);
                byte[] current = File.Exists(change.Path) ? File.ReadAllBytes(change.Path) : null;
                if (!Equal(current, change.After)) continue;
                if (change.Before == null) File.Delete(change.Path);
                else WriteShortcut(change.Path, change.Before);
            }
        }

        private void BackupValue(string path, string name)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path))
            {
                bool exists = key != null && key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
                registry.Add(new RegistryValueBackup { Key = path, Name = name, Existed = exists,
                    Value = exists ? key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null,
                    Kind = exists ? key.GetValueKind(name) : RegistryValueKind.String });
            }
        }

        private static readonly string[] ManagedUninstallNames = { "DisplayName", "DisplayVersion", "Publisher", "InstallLocation", "DisplayIcon", "UninstallString", "NoModify", "NoRepair", "EstimatedSize", "InstallDate" };

        internal static void Remove(Receipt receipt, string root)
        {
            foreach (OwnedFile shortcut in receipt.Shortcuts)
            {
                if (!shortcut.Path.Equals(StartMenuShortcut, StringComparison.OrdinalIgnoreCase) &&
                    !shortcut.Path.Equals(DesktopShortcut, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The uninstall receipt contains an unexpected shortcut path.");
            }
            foreach (OwnedFile shortcut in receipt.Shortcuts)
            {
                if (FileProof.Matches(shortcut.Path, shortcut)) File.Delete(shortcut.Path);
            }
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key != null && string.Equals(Convert.ToString(key.GetValue("IPMsgDock"), CultureInfo.InvariantCulture), StartupCommand(root), StringComparison.OrdinalIgnoreCase))
                    key.DeleteValue("IPMsgDock", false);
            }
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallKey, true))
            {
                if (key != null && string.Equals(Convert.ToString(key.GetValue("InstallLocation"), CultureInfo.InvariantCulture), root, StringComparison.OrdinalIgnoreCase))
                    foreach (string name in ManagedUninstallNames) key.DeleteValue(name, false);
            }
            RemoveEmptyRegistryKey(UninstallKey);
        }

        private static void RemoveEmptyRegistryKey(string path)
        {
            bool empty;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path))
                empty = key != null && key.ValueCount == 0 && key.SubKeyCount == 0;
            if (empty) Registry.CurrentUser.DeleteSubKey(path, false);
        }

        private static bool Equal(byte[] left, byte[] right)
        {
            if (left == null || right == null) return left == right;
            return left.SequenceEqual(right);
        }

        internal static void WriteShortcut(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            SafePaths.NoReparse(path);
            string temporary = path + ".ipmsgdock-" + Guid.NewGuid().ToString("N") + ".tmp";
            bool created = false;
            try
            {
                SafePaths.NoReparse(temporary);
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    output.Write(bytes, 0, bytes.Length);
                }
                // Replace directory entries, so a pre-existing hard link is never written through.
                SafePaths.NoReparse(path);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                created = false;
            }
            finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static void CreateShortcut(string path, string executable, string workingDirectory)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                Type type = Type.GetTypeFromProgID("WScript.Shell", true);
                shell = Activator.CreateInstance(type);
                shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { executable });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Open IPMsg Dock" });
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { executable + ",0" });
                // The app owns the configurable global shortcut. A .lnk hotkey would compete with it.
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, new object[0]);
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
