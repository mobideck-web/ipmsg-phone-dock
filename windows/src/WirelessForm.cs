using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IPMsg.Dock
{
    internal sealed class WirelessForm : Form
    {
        private readonly AdbBridge bridge;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly TextBox pairAddress, code, connectAddress;
        private readonly Label status;
        private readonly Button pair, connect, advanced;
        private readonly Panel manual;
        private bool busy;
        public string ConnectionEndpoint { get; private set; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr parameter, string text);

        public WirelessForm(AdbBridge bridge)
        {
            this.bridge = bridge;
            Text = "Connect your phone over Wi-Fi"; ClientSize = new Size(450, 520);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; BackColor = DockForm.Background; ForeColor = Color.White; Font = new Font("Segoe UI", 10);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 11 };
            for (int row = 0; row < 10; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = "Pair once. Connect in one click next time.", Font = new Font("Segoe UI Semibold", 13), AutoSize = true, Margin = new Padding(0, 0, 0, 12) });
            var scan = DockForm.Button("Scan QR · recommended", delegate {
                if(busy) return;
                using(var qr = new QrPairForm(bridge)) if(qr.ShowDialog(this)==DialogResult.OK) Complete(qr.ConnectionEndpoint);
            }); layout.Controls.Add(scan);
            layout.Controls.Add(Note("On your phone: Settings → Developer options → Wireless debugging → Pair device with pairing code."));
            layout.Controls.Add(Note("Pairing address & port", 14));
            pairAddress = Input("Address shown in the pairing dialog"); layout.Controls.Add(pairAddress);
            var pairingRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
            code = Input("6-digit code"); code.Dock = DockStyle.None; code.Width = 128; code.MaxLength = 6; code.UseSystemPasswordChar = true;
            pair = DockForm.Button("Pair & connect", async delegate { await PairAndConnect(); });
            pairingRow.Controls.Add(code); pairingRow.Controls.Add(pair); layout.Controls.Add(pairingRow);
            status = Note("Android 11+ · same Wi-Fi or hotspot · no mobile data needed", 14); status.ForeColor = DockForm.Accent; layout.Controls.Add(status);
            advanced = DockForm.Button("Enter connection address manually ▾", delegate { ShowManual(!manual.Visible); }); layout.Controls.Add(advanced);
            manual = new Panel { Dock = DockStyle.Top, Height = 99, Visible = false, Margin = new Padding(0, 5, 0, 0) };
            var manualLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            manualLayout.Controls.Add(Note("Main Wireless debugging screen → IP address & port"));
            connectAddress = Input("Connection address & port (different from pairing)"); manualLayout.Controls.Add(connectAddress);
            connect = DockForm.Button("Connect paired phone", async delegate { await ConnectManual(); }); manualLayout.Controls.Add(connect);
            manual.Controls.Add(manualLayout); layout.Controls.Add(manual);
            layout.Controls.Add(Note("Android 10: use USB. Keep Wireless debugging enabled to reconnect. Revoke this PC in Paired devices when you no longer trust it.", 12));
            Controls.Add(layout); AcceptButton = pair;
            Shown += delegate { Native.DarkTitle(Handle); pairAddress.Focus(); };
            FormClosing += delegate { cancellation.Cancel(); };
        }

        private async Task PairAndConnect()
        {
            if (busy) return;
            CancellationToken token = cancellation.Token;
            SetBusy(true); ShowStatus("Pairing with your phone…");
            try
            {
                string endpoint = pairAddress.Text.Trim();
                AdbBridge.ValidateEndpoint(endpoint);
                string host = endpoint.Substring(0, endpoint.LastIndexOf(':'));
                List<MdnsService> before = await Discover(token);
                await bridge.PairAsync(endpoint, code.Text.Trim(), token);
                code.Clear(); ShowStatus("Paired. Finding its connection address…");
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    List<MdnsService> found = (await Discover(token)).Where(s => s.Endpoint.StartsWith(host + ":", StringComparison.Ordinal)).ToList();
                    List<MdnsService> fresh = found.Where(s => !before.Any(b => b.Name == s.Name && b.Endpoint == s.Endpoint)).ToList();
                    MdnsService service = fresh.Count == 1 ? fresh[0] : found.Count == 1 ? found[0] : null;
                    if (service != null)
                    {
                        try
                        {
                            await bridge.ConnectAsync(service.Endpoint, token);
                            Complete(service.Endpoint); return;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { }
                    }
                    if (attempt < 5) await Task.Delay(700, token);
                }
                ShowManual(true);
                ShowStatus("Paired. Your network blocked automatic discovery. Close the phone's pairing dialog and enter the main Wireless debugging screen's connection address below.");
                connectAddress.Focus();
            }
            catch (OperationCanceledException) { ShowStatus("Cancelled."); }
            catch (Exception error) { ShowStatus(error.Message); }
            finally { code.Clear(); SetBusy(false); }
        }

        private async Task<List<MdnsService>> Discover(CancellationToken token)
        {
            try { return await bridge.MdnsServicesAsync(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return new List<MdnsService>(); }
        }

        private async Task ConnectManual()
        {
            if (busy) return;
            SetBusy(true); ShowStatus("Connecting…");
            try
            {
                string endpoint = connectAddress.Text.Trim();
                await bridge.ConnectAsync(endpoint, cancellation.Token);
                Complete(endpoint);
            }
            catch (OperationCanceledException) { ShowStatus("Cancelled."); }
            catch (Exception error) { ShowStatus(error.Message); }
            finally { SetBusy(false); }
        }

        private void Complete(string endpoint)
        {
            if (IsDisposed || cancellation.IsCancellationRequested) return;
            ConnectionEndpoint = endpoint; DialogResult = DialogResult.OK; Close();
        }
        private void SetBusy(bool value)
        {
            busy = value;
            if (IsDisposed) return;
            pair.Enabled = connect.Enabled = advanced.Enabled = pairAddress.Enabled = code.Enabled = connectAddress.Enabled = !value;
        }
        private void ShowStatus(string message) { if (!IsDisposed) status.Text = message; }
        private void ShowManual(bool show)
        {
            if (IsDisposed) return;
            manual.Visible = show; ClientSize = new Size(450, show ? 615 : 520);
            advanced.Text = show ? "Hide manual connection ▴" : "Enter connection address manually ▾";
        }
        private static Label Note(string text, int top = 0)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(400, 0), Dock = DockStyle.Top, Margin = new Padding(0, top, 0, 8) };
        }
        private static TextBox Input(string cue)
        {
            var input = new TextBox { Text = "", Dock = DockStyle.Top, BackColor = DockForm.Card, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 3, 5, 3) };
            input.HandleCreated += delegate { SendMessage(input.Handle, 0x1501, new IntPtr(1), cue); };
            return input;
        }
    }
}
