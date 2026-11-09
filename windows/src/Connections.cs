using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace IPMsg.Dock
{
    // Convenience preferences only. Device authorization remains Android's RSA/TLS debugging
    // approval; the stable identifier below is not a substitute for transport authentication.
    public sealed class Connections
    {
        private sealed class Remembered
        {
            public string Identity, Serial, Model, Endpoint, MdnsName, HardwareSerial;
            public bool Trusted;
            public long Updated;
        }
        private readonly AdbBridge bridge;
        private readonly string preferences;
        private readonly object gate = new object();
        private readonly List<Remembered> devices = new List<Remembered>();
        private readonly Dictionary<string, string> hardwareIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private const int RecoveryDeadlineMilliseconds = 45000;
        private List<MdnsService> discovery = new List<MdnsService>();
        private string preferred = "";
        public string PreferredSerial { get { lock (gate) return preferred; } }

        public Connections(AdbBridge bridge, string stateDirectory)
        {
            if (bridge == null) throw new ArgumentNullException("bridge");
            this.bridge = bridge;
            Directory.CreateDirectory(stateDirectory);
            preferences = Path.Combine(Path.GetFullPath(stateDirectory), "connections.xml");
            Load();
        }

        public bool IsTrusted(string identity)
        {
            lock (gate) return !String.IsNullOrEmpty(identity) && devices.Any(d => d.Identity == identity && d.Trusted);
        }

        public void Trust(string identity, PhoneDevice device) { Store(identity, device, true); }
        public void Remember(PhoneDevice device, string identity) { Store(identity, device, false); }
        public void Forget()
        {
            lock (gate) { devices.Clear(); hardwareIds.Clear(); preferred = ""; Save(); }
        }

        private void Store(string identity, PhoneDevice device, bool trusted)
        {
            if (device == null || !ValidIdentity(identity)) throw new ArgumentException("An authorized phone identity is required.");
            AdbBridge.ValidateSerial(device.Serial);
            lock (gate)
            {
                Remembered entry = devices.FirstOrDefault(d => d.Identity == identity);
                if (entry == null) { entry = new Remembered { Identity = identity }; devices.Add(entry); }
                entry.Serial = device.Serial;
                entry.Model = device.Model ?? "";
                entry.Trusted = entry.Trusted || trusted;
                entry.Updated = DateTime.UtcNow.Ticks;
                string hardware;
                if (hardwareIds.TryGetValue(identity, out hardware)) entry.HardwareSerial = hardware;
                try { AdbBridge.ValidateEndpoint(device.Serial); entry.Endpoint = device.Serial; } catch (ArgumentException) { }
                MdnsService service = discovery.FirstOrDefault(s => s.Endpoint == device.Serial || s.Name == device.Serial ||
                    device.Serial == s.Name + "." + s.Type || SameHardware(s.Name, entry.HardwareSerial));
                if (service != null) { entry.MdnsName = service.Name; entry.Endpoint = service.Endpoint; }
                preferred = device.Serial;
                while (devices.Count > 32) devices.Remove(devices.OrderBy(d => d.Updated).First());
                Save();
            }
        }

        public async Task<string> IdentityAsync(string serial, CancellationToken token)
        {
            CommandResult result = await bridge.ShellAsync(serial,
                "getprop ro.serialno\ngetprop ro.boot.serialno\nsettings get secure android_id 2>/dev/null || true", token, 12000).ConfigureAwait(false);
            result.EnsureSuccess("Could not identify the authorized phone");
            string[] lines = result.Output.Replace("\r", "").Split('\n');
            string hardware = lines.Length > 0 && ValidIdentifier(lines[0]) ? lines[0].Trim() :
                lines.Length > 1 && ValidIdentifier(lines[1]) ? lines[1].Trim() : "";
            string androidId = lines.Length > 2 && ValidIdentifier(lines[2]) ? lines[2].Trim() : "";
            if (hardware.Length == 0 && androidId.Length == 0) throw new IOException("The phone did not provide a stable identifier. Reconnect and approve debugging on the phone.");
            string raw = hardware.Length > 0 ? "serial:" + hardware : "android-id:" + androidId;
            string identity;
            using (SHA256 hash = SHA256.Create()) identity = "android:" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(raw))).Replace("-", "").ToLowerInvariant();
            lock (gate) hardwareIds[identity] = hardware;
            return identity;
        }

        private static bool ValidIdentifier(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 200) return false;
            string clean = value.Trim();
            return clean != "null" && clean != "unknown" && clean != "0" && !clean.Any(Char.IsControl);
        }

        private static bool ValidIdentity(string identity)
        {
            return identity != null && identity.Length == 72 && identity.StartsWith("android:", StringComparison.Ordinal) &&
                identity.Substring(8).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }

        public async Task<PhoneDevice> ResolveAsync(string preferredSerial, CancellationToken token)
        {
            using (CancellationTokenSource recovery = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                recovery.CancelAfter(RecoveryDeadlineMilliseconds);
                try { return await ResolveWithinDeadlineAsync(preferredSerial, recovery.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) throw;
                    throw new IOException("The phone did not reconnect in time. Check its USB cable or Wireless debugging, then Connect again.");
                }
            }
        }

        private async Task<PhoneDevice> ResolveWithinDeadlineAsync(string preferredSerial, CancellationToken token)
        {
            List<Remembered> known;
            string requested;
            lock (gate)
            {
                // Store() can update a remembered transport while discovery is awaiting ADB.
                // Resolve against one identity/endpoint snapshot rather than mutable records.
                known = devices.OrderByDescending(d => d.Updated).Select(d => new Remembered {
                    Identity = d.Identity, Serial = d.Serial, Model = d.Model, Endpoint = d.Endpoint,
                    MdnsName = d.MdnsName, HardwareSerial = d.HardwareSerial, Trusted = d.Trusted, Updated = d.Updated }).ToList();
                requested = String.IsNullOrEmpty(preferredSerial) ? preferred : preferredSerial;
            }
            List<PhoneDevice> connected = await bridge.DevicesAsync(token).ConfigureAwait(false);
            PhoneDevice selected = await ChooseAsync(connected, known, requested, token).ConfigureAwait(false);
            if (selected != null) return selected;
            if (String.IsNullOrEmpty(requested) && connected.Count(d => d.State == "device") > 1) throw new IOException("Several phones are connected. Choose the phone you want to open.");

            List<MdnsService> found = new List<MdnsService>();
            try { found = await bridge.MdnsServicesAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { } // Manual remembered endpoint remains available when multicast is blocked.
            lock (gate) discovery = found;
            List<string> candidates = new List<string>();
            IEnumerable<Remembered> reconnect = String.IsNullOrEmpty(requested) ? known : known.Where(d => d.Serial == requested || d.Endpoint == requested);
            foreach (Remembered entry in reconnect)
            {
                foreach (MdnsService service in found)
                    if (service.Name == entry.MdnsName || SameHardware(service.Name, entry.HardwareSerial)) AddEndpoint(candidates, service.Endpoint);
                AddEndpoint(candidates, entry.Endpoint);
            }
            foreach (string endpoint in candidates.Take(4))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    // ADB can retain a stale Wi-Fi transport and report "already connected"
                    // indefinitely. Reset only that offline endpoint in our private server.
                    // Never reset other phones, USB authorization, or the user's ADB server.
                    if (connected.Any(d => d.Serial == endpoint && d.State == "offline"))
                        await bridge.DisconnectAsync(endpoint, token).ConfigureAwait(false);
                    await bridge.ConnectAsync(endpoint, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { continue; }
                connected = await bridge.DevicesAsync(token).ConfigureAwait(false);
                selected = await ChooseAsync(connected, known, requested, token).ConfigureAwait(false);
                if (selected != null) return selected;
            }
            PhoneDevice unavailable = connected.FirstOrDefault(d => d.Serial == requested);
            if (unavailable != null && unavailable.State == "unauthorized")
                throw new IOException("Unlock the selected phone and approve this PC's debugging key, then Connect again.");
            if (unavailable != null && unavailable.State == "offline")
                throw new IOException("The selected phone is not responding. Check USB or Wireless debugging, then Connect again.");
            if (!String.IsNullOrEmpty(requested)) throw new IOException("Phone unavailable. Reconnect USB or choose More → Connection to set up Wi-Fi or choose a phone.");
            if (connected.Any(d => d.State == "unauthorized")) throw new IOException("Unlock the phone and approve this PC's debugging key, then Connect again.");
            throw new IOException("Phone not available. Connect USB, or enable Wireless debugging on the same Wi-Fi. Pair once if this is a new phone.");
        }

        private async Task<PhoneDevice> ChooseAsync(List<PhoneDevice> connected, List<Remembered> known, string requested, CancellationToken token)
        {
            List<PhoneDevice> ready = connected.Where(d => d.State == "device").ToList();
            // A transport is not a phone identity. The same authorized phone can be
            // ready over USB while its old Wi-Fi address remains listed as offline.
            // Check verified alternatives before treating the stale transport as failure.
            PhoneDevice exact = ready.FirstOrDefault(d => d.Serial == requested);
            Remembered wanted = known.FirstOrDefault(d => d.Serial == requested || d.Endpoint == requested);
            Dictionary<string, string> observed = new Dictionary<string, string>(StringComparer.Ordinal);
            if (exact != null)
            {
                if (wanted == null) return Verified(exact, await IdentityAsync(exact.Serial, token).ConfigureAwait(false));
                string identity = "";
                try { identity = await IdentityAsync(exact.Serial, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { } // A stale "device" transport must not block a verified alternate.
                observed[exact.Serial] = identity;
                if (identity == wanted.Identity) return Verified(exact, identity);
                if (identity.Length != 0)
                    throw new IOException("This address now belongs to a different phone. Forget the remembered phone before approving its replacement.");
            }
            if (!String.IsNullOrEmpty(requested) && wanted == null) return null;
            foreach (Remembered entry in wanted == null ? known : new List<Remembered> { wanted })
            {
                foreach (PhoneDevice phone in ready)
                {
                    try
                    {
                        string identity;
                        if (!observed.TryGetValue(phone.Serial, out identity))
                        {
                            identity = await IdentityAsync(phone.Serial, token).ConfigureAwait(false);
                            observed[phone.Serial] = identity;
                        }
                        if (identity == entry.Identity) return Verified(phone, identity);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { observed[phone.Serial] = ""; }
                }
            }
            return String.IsNullOrEmpty(requested) && ready.Count == 1 ?
                Verified(ready[0], await IdentityAsync(ready[0].Serial, token).ConfigureAwait(false)) : null;
        }

        private static PhoneDevice Verified(PhoneDevice phone, string identity)
        {
            return new PhoneDevice { Serial = phone.Serial, State = phone.State, Model = phone.Model, VerifiedIdentity = identity };
        }

        private static bool SameHardware(string serviceName, string hardware)
        {
            return !String.IsNullOrEmpty(hardware) && !String.IsNullOrEmpty(serviceName) &&
                serviceName.StartsWith("adb-" + hardware + "-", StringComparison.Ordinal);
        }
        private static void AddEndpoint(List<string> candidates, string endpoint)
        {
            try { AdbBridge.ValidateEndpoint(endpoint); if (!candidates.Contains(endpoint)) candidates.Add(endpoint); }
            catch (ArgumentException) { }
        }

        private void Load()
        {
            if (!File.Exists(preferences)) return;
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 262144 };
                XmlDocument document = new XmlDocument { XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(preferences, settings)) document.Load(reader);
                XmlElement root = document.DocumentElement;
                if (root == null || root.Name != "connections") return;
                preferred = root.GetAttribute("preferred");
                foreach (XmlNode node in root.ChildNodes)
                {
                    XmlElement element = node as XmlElement;
                    if (element == null || element.Name != "phone" || devices.Count >= 32) continue;
                    string identity = element.GetAttribute("identity"), serial = element.GetAttribute("serial");
                    if (!ValidIdentity(identity)) continue;
                    try { AdbBridge.ValidateSerial(serial); } catch (ArgumentException) { continue; }
                    long updated; Int64.TryParse(element.GetAttribute("updated"), out updated);
                    devices.Add(new Remembered { Identity = identity, Serial = serial, Model = element.GetAttribute("model"),
                        Endpoint = element.GetAttribute("endpoint"), MdnsName = element.GetAttribute("mdns"), HardwareSerial = element.GetAttribute("hardware"),
                        Trusted = element.GetAttribute("trusted") == "true", Updated = updated });
                }
            }
            catch (XmlException) { devices.Clear(); preferred = ""; }
            catch (IOException) { devices.Clear(); preferred = ""; }
            catch (UnauthorizedAccessException) { devices.Clear(); preferred = ""; }
        }

        private void Save()
        {
            string temporary = preferences + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                XmlWriterSettings settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (XmlWriter writer = XmlWriter.Create(stream, settings))
                {
                    writer.WriteStartElement("connections"); writer.WriteAttributeString("preferred", preferred ?? "");
                    foreach (Remembered device in devices)
                    {
                        writer.WriteStartElement("phone");
                        writer.WriteAttributeString("identity", device.Identity); writer.WriteAttributeString("serial", device.Serial);
                        writer.WriteAttributeString("model", device.Model ?? ""); writer.WriteAttributeString("endpoint", device.Endpoint ?? "");
                        writer.WriteAttributeString("mdns", device.MdnsName ?? ""); writer.WriteAttributeString("hardware", device.HardwareSerial ?? "");
                        writer.WriteAttributeString("trusted", device.Trusted ? "true" : "false");
                        writer.WriteAttributeString("updated", device.Updated.ToString(CultureInfo.InvariantCulture)); writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                if (File.Exists(preferences)) File.Replace(temporary, preferences, null);
                else File.Move(temporary, preferences);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
