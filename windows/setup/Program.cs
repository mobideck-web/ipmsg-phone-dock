using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace IPMsg.Dock.Setup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
#if UNINSTALLER
                return Uninstaller.Run(args);
#else
                // Verification only reads the embedded package; it never installs or touches HKCU.
                if (args.Length == 2 && args[0] == "--verify")
                {
                    string report = Path.GetFullPath(args[1]);
                    Receipt package = ReadPackage();
                    using (Stream zip = Resource("IPMsgDock.Setup.Payload.zip")) InstallerEngine.VerifyArchive(zip, package, null);
                    using (FileStream output = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (StreamWriter writer = new StreamWriter(output, new UTF8Encoding(false)))
                        writer.WriteLine("PASS: " + BuildInfo.Version + "; " + package.Files.Count + " embedded files; every size and SHA-256 hash verified.");
                    return 0;
                }
                if (args.Length != 0) throw new ArgumentException("Open Setup.exe normally, or use --verify <new-report-file> for package verification.");
                Application.Run(new SetupForm());
                return 0;
#endif
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "IPMsg Dock Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        internal static Stream Resource(string name)
        {
            Stream result = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (result == null) throw new InvalidDataException("An installation resource is missing: " + name);
            return result;
        }

        internal static Receipt ReadPackage()
        {
            using (Stream manifest = Resource("IPMsgDock.Setup.Payload.tsv")) return Receipt.Read(manifest, true);
        }
    }

#if !UNINSTALLER
    internal sealed class SetupForm : Form
    {
        private readonly CheckBox desktop = new CheckBox();
        private readonly CheckBox startup = new CheckBox();
        private readonly CheckBox launch = new CheckBox();
        private readonly Button install = new Button();
        private readonly Button cancel = new Button();
        private readonly Label status = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private bool working;
        private bool installed;

        internal SetupForm()
        {
            Text = "Install IPMsg Dock";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Segoe UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(590, 445);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(248, 250, 252);
            Label title = new Label { Text = "IPMsg Dock", Font = new Font(Font.FontFamily, 23F, FontStyle.Bold), AutoSize = true, Location = new Point(28, 22) };
            Label description = new Label { Text = "Android phone controls for Windows  •  " + BuildInfo.Version,
                AutoSize = false, Location = new Point(30, 76), Size = new Size(530, 27) };
            Label destination = new Label { Text = "Install for your Windows account:\r\n" + WindowsIntegration.InstallRoot,
                AutoEllipsis = true, Location = new Point(30, 119), Size = new Size(530, 51) };
            Label startMenu = new Label { Text = "A Start menu shortcut will be created.", AutoSize = true, Location = new Point(30, 186) };
            desktop.Text = "Create a Desktop shortcut";
            desktop.Checked = true;
            desktop.SetBounds(30, 218, 520, 28);
            startup.Text = "Start in the background when I sign in";
            startup.Checked = false;
            startup.SetBounds(30, 250, 520, 28);
            Label help = new Label { Text = "Use the tray icon to reopen or exit. Change the keyboard shortcut in the app.\r\nUpdates and uninstall keep your pairing keys, settings, and history.",
                Location = new Point(30, 290), Size = new Size(530, 48), ForeColor = Color.FromArgb(71, 85, 105), Font = new Font(Font.FontFamily, 9F) };
            status.SetBounds(30, 346, 530, 25);
            status.Text = "Ready to install. No administrator account is required.";
            status.Font = new Font(Font.FontFamily, 9F);
            progress.SetBounds(30, 376, 325, 12);
            progress.Visible = false;
            launch.Text = "Open IPMsg Dock";
            launch.Checked = true;
            launch.SetBounds(30, 376, 315, 28);
            launch.Visible = false;
            install.Text = "Install";
            install.SetBounds(370, 387, 95, 34);
            install.Click += InstallClick;
            cancel.Text = "Cancel";
            cancel.SetBounds(473, 387, 88, 34);
            cancel.Click += delegate { if (!working) Close(); };
            Controls.AddRange(new Control[] { title, description, destination, startMenu, desktop, startup, help, status, progress, launch, install, cancel });
            AcceptButton = install;
            CancelButton = cancel;
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (working) e.Cancel = true; };
        }

        private void InstallClick(object sender, EventArgs args)
        {
            if (installed)
            {
                if (launch.Checked) Process.Start(new ProcessStartInfo(Path.Combine(WindowsIntegration.InstallRoot, "IPMsgDock.exe")) { WorkingDirectory = WindowsIntegration.InstallRoot, UseShellExecute = true });
                Close();
                return;
            }
            working = true;
            install.Enabled = cancel.Enabled = desktop.Enabled = startup.Enabled = false;
            progress.Visible = true;
            progress.Style = ProgressBarStyle.Marquee;
            status.Text = "Verifying and installing the offline package…";
            InstallOptions options = new InstallOptions { Desktop = desktop.Checked, StartAtSignIn = startup.Checked };
            BackgroundWorker worker = new BackgroundWorker();
            // WScript shortcuts use COM. Keep this work on the form's STA thread via BeginInvoke;
            // the heavy, independent ZIP verification and file operations run in this worker.
            worker.DoWork += delegate
            {
                using (SetupOperation operation = new SetupOperation())
                {
                Receipt package = Program.ReadPackage();
                string root = SafePaths.Root(WindowsIntegration.InstallRoot);
                string stage = Path.Combine(Path.GetDirectoryName(root), ".IPMsgDock-stage-" + Guid.NewGuid().ToString("N"));
                try
                {
                    using (Stream zip = Program.Resource("IPMsgDock.Setup.Payload.zip")) InstallerEngine.VerifyArchive(zip, package, stage);
                    WindowsIntegration integration = new WindowsIntegration(root, options);
                    Invoke((MethodInvoker)delegate { integration.Prepare(InstallerEngine.Previous(root), stage); });
                    InstallerEngine.InstallFiles(stage, root, package,
                        delegate(Receipt previous, Receipt receipt) { Invoke((MethodInvoker)delegate { integration.Apply(previous, receipt); }); },
                        delegate { Invoke((MethodInvoker)integration.Undo); });
                }
                finally { InstallerEngine.CleanupOwnedTree(stage, package.Files.Select(delegate(OwnedFile f) { return f.Path; }).Concat(new[] { ".shortcut-start.lnk", ".shortcut-desktop.lnk" })); }
                }
            };
            worker.RunWorkerCompleted += delegate(object completedSender, RunWorkerCompletedEventArgs completed)
            {
                working = false;
                progress.Visible = false;
                install.Enabled = cancel.Enabled = true;
                if (completed.Error != null)
                {
                    desktop.Enabled = startup.Enabled = true;
                    status.Text = "Installation did not finish. Resolve the message, then retry.";
                    Exception error = completed.Error;
                    while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                    MessageBox.Show(this, error.Message, "IPMsg Dock Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    installed = true;
                    status.Text = "Installed. IPMsg Dock is available from your Start menu.";
                    install.Text = "Finish";
                    cancel.Visible = false;
                    launch.Visible = true;
                }
                worker.Dispose();
            };
            worker.RunWorkerAsync();
        }
    }
#endif

    internal static class Uninstaller
    {
        internal static int Run(string[] args)
        {
            string root = SafePaths.Root(WindowsIntegration.InstallRoot);
            string current = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            string installedUninstaller = Path.Combine(root, "Uninstall.exe");
            if (current.Equals(installedUninstaller, StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length != 0) throw new ArgumentException("Open Uninstall.exe without arguments.");
                // A separate managed process can remove the installed executable without shell commands.
                // Windows may retain this small running helper in its ordinary temporary-file directory.
                string helper = Path.Combine(Path.GetTempPath(), "IPMsgDock-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                SafePaths.NoReparse(helper);
                using (FileStream source = File.OpenRead(current))
                using (FileStream destination = new FileStream(helper, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(destination);
                Process.Start(new ProcessStartInfo(helper, "--uninstall-worker " + Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture)) { UseShellExecute = false });
                return 0;
            }
            int parent;
            if (args.Length != 2 || args[0] != "--uninstall-worker" || !int.TryParse(args[1], out parent))
                throw new ArgumentException("Run Uninstall.exe from the IPMsg Dock installation, or use Windows Settings > Apps.");
            try { using (Process process = Process.GetProcessById(parent)) if (!process.WaitForExit(10000)) throw new IOException("The uninstall launcher has not closed. Try again."); }
            catch (ArgumentException) { }
            using (SetupOperation operation = new SetupOperation())
            {
            string receiptPath = SafePaths.Under(root, Receipt.FileName);
            Receipt receipt;
            using (FileStream file = File.OpenRead(receiptPath)) receipt = Receipt.Read(file, false);
            foreach (OwnedFile shortcut in receipt.Shortcuts)
            {
                if (!shortcut.Path.Equals(WindowsIntegration.StartMenuShortcut, StringComparison.OrdinalIgnoreCase) &&
                    !shortcut.Path.Equals(WindowsIntegration.DesktopShortcut, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The uninstall receipt contains an unexpected shortcut path.");
                SafePaths.NoReparse(shortcut.Path);
            }
            if (MessageBox.Show("Uninstall IPMsg Dock for this Windows account?\r\n\r\nYour pairing keys, settings, and history will be kept.", "Uninstall IPMsg Dock",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return 0;
            InstallerEngine.EnsureClosed(root, receipt.Files);
            System.Collections.Generic.List<string> preserved = InstallerEngine.RemoveInstalledFiles(root, receipt);
            WindowsIntegration.Remove(receipt, root);
            File.Delete(receiptPath);
            InstallerEngine.RemoveEmptyDirectories(root, receipt.Files.Select(delegate(OwnedFile f) { return f.Path; }).Concat(new[] { Receipt.FileName }));
            MessageBox.Show(preserved.Count == 0 ? "IPMsg Dock was uninstalled. Your pairing keys, settings, and history were kept." :
                "IPMsg Dock was uninstalled. Modified files were kept in the installation folder. Your pairing keys, settings, and history were kept.",
                "IPMsg Dock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
            }
        }
    }
}
