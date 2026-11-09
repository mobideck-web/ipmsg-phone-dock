using System;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using QRCoder;

namespace IPMsg.Dock
{
    internal sealed class QrPairForm : Form
    {
        private readonly AdbBridge bridge;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        private readonly string service, secret;
        private readonly Label status;
        private readonly PictureBox image;
        public string ConnectionEndpoint { get; private set; }
        public QrPairForm(AdbBridge bridge)
        {
            this.bridge = bridge; service = "studio-" + RandomHex(10); secret = RandomHex(16);
            Text = "Scan to connect"; ClientSize = new Size(400, 465); StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            BackColor = GlassTheme.Background; ForeColor = GlassTheme.Text; Font = new Font("Segoe UI",10);
            var title = new Label { Text = "Connect over Wi-Fi", Font = new Font("Segoe UI Semibold",17), AutoSize = true, Location = new Point(28,20) };
            var note = new Label { Text = "Phone Settings → Developer options → Wireless debugging → Pair device with QR code", Location = new Point(28,63), Size = new Size(344,48) };
            image = new PictureBox { Location = new Point(72,121), Size = new Size(256,256), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, AccessibleName = "Temporary local ADB pairing QR code" };
            using (var generator = new QRCodeGenerator())
            using (var data = generator.CreateQrCode(Payload(service,secret),QRCodeGenerator.ECCLevel.M))
            using (var qr = new QRCode(data)) image.Image = qr.GetGraphic(6, Color.Black, Color.White, true);
            status = new Label { Text = "Same local Wi-Fi · expires in 3 minutes", Location = new Point(28,393), Size = new Size(344,56), ForeColor = GlassTheme.MutedText };
            Controls.AddRange(new Control[]{title,note,image,status});
            Shown += async delegate { Native.DarkTitle(Handle); await PairAsync(); };
            FormClosing += delegate { lifetime.Cancel(); };
            FormClosed += delegate { if(image.Image!=null) { image.Image.Dispose(); image.Image=null; } };
        }
        internal static string Payload(string service,string secret)
        {
            if(service==null || !System.Text.RegularExpressions.Regex.IsMatch(service,"^studio-[0-9a-f]{20}$") ||
                secret==null || !System.Text.RegularExpressions.Regex.IsMatch(secret,"^[0-9a-f]{32}$")) throw new ArgumentException("Invalid QR pairing identity.");
            return "WIFI:T:ADB;S:"+service+";P:"+secret+";;";
        }
        private static string RandomHex(int size) { byte[] bytes=new byte[size];using(var random=RandomNumberGenerator.Create()) random.GetBytes(bytes);return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant(); }
        private async Task PairAsync()
        {
            CancellationToken token = lifetime.Token;
            try {
                await bridge.EnsureReadyAsync(token);
                string host = null;
                while(host==null) {
                    token.ThrowIfCancellationRequested();
                    var found = await bridge.MdnsServicesAsync(token,true);
                    var matching = found.Where(s=>s.Type=="_adb-tls-pairing._tcp" && s.Name==service).ToList();
                    if(matching.Count>1) throw new InvalidOperationException("More than one phone scanned this QR. Close this window and generate a new code.");
                    if(matching.Count==1) {
                        status.Text="Pairing with the phone that scanned this code…";
                        await bridge.PairQrAsync(matching[0].Endpoint,secret,token);
                        host=matching[0].Endpoint.Substring(0,matching[0].Endpoint.LastIndexOf(':'));
                        if(image.Image!=null) { image.Image.Dispose(); image.Image=null; }
                        break;
                    }
                    await Task.Delay(900,token);
                }
                status.Text="Paired · finding the phone's connection…";
                for(int attempt=0;attempt<10;attempt++) {
                    token.ThrowIfCancellationRequested();
                    foreach(var entry in (await bridge.MdnsServicesAsync(token)).Where(s=>s.Endpoint.StartsWith(host+":",StringComparison.Ordinal))) {
                        try { await bridge.ConnectAsync(entry.Endpoint,token); ConnectionEndpoint=entry.Endpoint; DialogResult=DialogResult.OK; Close(); return; }
                        catch(OperationCanceledException) { throw; } catch(Exception) { }
                    }
                    await Task.Delay(800,token);
                }
                status.Text="Paired. Close this window, then use the connection address from the phone's main Wireless debugging screen.";
            }
            catch(OperationCanceledException) { if(!IsDisposed) { image.Visible=false;status.Text="QR expired. Close this window and open Scan QR again."; } }
            catch(Exception) { if(!IsDisposed) { image.Visible=false;status.Text="Could not pair. Check Wireless debugging, the same local network and mDNS access. Close and try a new QR, or use a pairing code."; } }
        }
    }
}
