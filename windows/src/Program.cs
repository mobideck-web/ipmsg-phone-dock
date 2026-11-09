using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace IPMsg.Dock
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (AdbProxy.IsInvocation) return AdbProxy.Run(args);
            bool background = Array.IndexOf(args, "--background") >= 0;
            using (SingleInstanceGate instance = new SingleInstanceGate())
            {
                if (!instance.IsOwner) { if (!background) instance.ActivateExisting(); return 0; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            { MessageBox.Show(e.Exception.Message, "IPMsg Dock", MessageBoxButtons.OK, MessageBoxIcon.Error); };
            try
            {
                string runtime = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "scrcpy");
                if (!File.Exists(Path.Combine(runtime, "scrcpy.exe")))
                    throw new IOException("Extract the complete ZIP first, then open IPMsgDock.exe. Keep its runtime folder beside it.");
                string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPMsgDock");
                Directory.CreateDirectory(state);
                using (var form = new DockForm(runtime, state, background)) Application.Run(form);
                return 0;
            }
            catch (Exception e)
            { MessageBox.Show(e.Message, "IPMsg Dock", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
            }
        }
    }
}
