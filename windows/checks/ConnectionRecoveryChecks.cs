using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IPMsg.Dock;

internal static class ConnectionRecoveryChecks
{
    private const string Wifi = "10.6.0.2:37121", NewWifi = "10.6.0.3:37122", Usb = "fixture-usb-a", Other = "fixture-usb-b";
    private static string root, scene;
    private static int passed, sequence;

    public static int Main()
    {
        try { RunAsync().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + passed + " focused connection recovery checks; no real phones or network used."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static async Task RunAsync()
    {
        root = AppDomain.CurrentDomain.BaseDirectory; scene = Path.Combine(root, "scene"); Directory.CreateDirectory(scene);
        Environment.SetEnvironmentVariable("IPMSG_CONNECTION_FIXTURE", scene);
        Connections known = await NewAsync(Wifi, "same-phone");
        Set("devices", Wifi + " offline\n" + Usb + " device model:Fixture\n");
        Set("identities", Wifi + "=same-phone\n" + Usb + "=same-phone\n");
        PhoneDevice selected = await known.ResolveAsync(Wifi, CancellationToken.None);
        Assert(selected.Serial == Usb && !Log().Contains(" connect "), "stale offline Wi-Fi transport resolves to the same authorized phone over USB without reconnecting");
        Assert(known.IsTrusted(await known.IdentityAsync(Usb, CancellationToken.None)), "alternate transport retains approval only through its matching stable identity");
        Set("identities", Usb + "=replacement-phone\n");
        Assert(selected.VerifiedIdentity != await known.IdentityAsync(Usb, CancellationToken.None) && known.IsTrusted(selected.VerifiedIdentity),
            "resolved phone pins its verified identity so the UI can reject an endpoint changing before final connection");

        known = await NewAsync(Usb, "same-phone");
        Set("devices", Usb + " unauthorized\n" + Wifi + " device\n"); Set("identities", Wifi + "=same-phone\n");
        selected = await known.ResolveAsync(Usb, CancellationToken.None);
        Assert(selected.Serial == Wifi, "an unauthorized stale transport does not block an already-authorized same-phone transport");

        known = await NewAsync(Wifi, "same-phone");
        Set("devices", Wifi + " device\n" + Usb + " device\n"); Set("identities", Usb + "=same-phone\n");
        selected = await known.ResolveAsync(Wifi, CancellationToken.None);
        Assert(selected.Serial == Usb, "a stale ready transport whose identity command fails can recover through a verified same-phone alternate");

        known = await NewAsync(Wifi, "same-phone");
        Set("devices", Wifi + " offline\n" + Other + " device\n"); Set("identities", Other + "=different-phone\n");
        await ExpectAsync<IOException>(known.ResolveAsync(Wifi, CancellationToken.None), "not responding");
        Assert(!Log().Contains("-s " + Other + " shell input"), "offline remembered phone never falls back to an unrelated authorized phone");

        known = await NewAsync(null, null);
        Set("devices", Wifi + " offline\n" + Other + " device\n"); Set("identities", Other + "=different-phone\n");
        await ExpectAsync<IOException>(known.ResolveAsync(Wifi, CancellationToken.None), "not responding");
        Assert(!Log().Contains(" shell "), "an unknown selected offline transport cannot borrow another phone's identity");

        known = await NewAsync(Wifi, "same-phone"); Set("devices", Wifi + " device\n"); Set("identities", Wifi + "=replacement-phone\n");
        await ExpectAsync<IOException>(known.ResolveAsync(Wifi, CancellationToken.None), "different phone");
        Assert(!Log().Contains(" mdns "), "reused address with a different identity fails closed before reconnect or fallback");

        known = await NewAsync(Wifi, "same-phone"); Set("devices", Wifi + " offline\n");
        Set("mdns", "adb-same-phone-random _adb-tls-connect._tcp " + NewWifi + "\n" +
            "adb-another-phone-random _adb-tls-connect._tcp 10.6.0.4:37123\n");
        Set("after-connect", NewWifi + " device\n"); Set("identities", NewWifi + "=same-phone\n");
        selected = await known.ResolveAsync(Wifi, CancellationToken.None);
        Assert(selected.Serial == NewWifi && Log().Contains("connect " + NewWifi) && !Log().Contains("connect 10.6.0.4"), "changed Wi-Fi address reconnects only the remembered phone's discovery identity");

        known = await NewAsync(Wifi, "same-phone"); Set("devices", Wifi + " offline\n");
        Set("after-connect", Wifi + " device\n"); Set("identities", Wifi + "=same-phone\n");
        selected = await known.ResolveAsync(Wifi, CancellationToken.None);
        string log = Log();
        Assert(selected.Serial == Wifi && log.IndexOf("disconnect " + Wifi, StringComparison.Ordinal) >= 0 &&
            log.IndexOf("disconnect " + Wifi, StringComparison.Ordinal) < log.IndexOf(" connect " + Wifi, StringComparison.Ordinal),
            "only the stale offline remembered Wi-Fi endpoint is disconnected before a bounded reconnect");

        known = await NewAsync(Usb, "same-phone"); Set("devices", Usb + " unauthorized\n");
        await ExpectAsync<IOException>(known.ResolveAsync(Usb, CancellationToken.None), "approve this PC");
        Assert(!Log().Contains("disconnect") && !Log().Contains(" connect "), "missing owner authorization remains actionable and never resets USB approval");

        known = await NewAsync(null, null); Set("devices", Usb + " device\n"); Set("identities", Usb + "=first-phone\n");
        selected = await known.ResolveAsync(null, CancellationToken.None);
        Assert(selected.Serial == Usb && !String.IsNullOrEmpty(selected.VerifiedIdentity), "first use verifies a single ready phone before the separate connection approval step");

        known = await NewAsync(null, null); Set("devices", Usb + " device\n" + Other + " device\n");
        await ExpectAsync<IOException>(known.ResolveAsync(null, CancellationToken.None), "Several phones");
        Assert(!Log().Contains(" shell "), "multiple unselected phones require explicit choice without probing identities");

        known = await NewAsync(Wifi, "same-phone"); Set("devices", Wifi + " offline\n"); Set("hang-command", "mdns");
        Stopwatch watch = Stopwatch.StartNew();
        using (CancellationTokenSource cancel = new CancellationTokenSource(200))
            await ExpectAsync<OperationCanceledException>(known.ResolveAsync(Wifi, cancel.Token), null);
        Assert(watch.ElapsedMilliseconds < 2200, "cancelling discovery settles promptly without blocking process cleanup");

        known = await NewAsync(null, null); Set("listing-mode", "truncated");
        await ExpectAsync<IOException>(known.ResolveAsync(null, CancellationToken.None), "incomplete");
        Assert(!Log().Contains(" mdns "), "truncated ADB listing is rejected before selecting or reconnecting a phone");

        known = await NewAsync(null, null); Set("devices", Usb + " offline\n" + Usb + " device\n");
        await ExpectAsync<IOException>(known.ResolveAsync(Usb, CancellationToken.None), "duplicate transports");
        Assert(!Log().Contains(" shell "), "conflicting duplicate transport states cannot select a phone");

        known = await NewAsync(null, null); Set("devices", String.Join("\n", Enumerable.Range(0, 129).Select(n => "fixture-" + n + " device")));
        await ExpectAsync<IOException>(known.ResolveAsync(null, CancellationToken.None), "Too many");
        Assert(!Log().Contains(" shell "), "transport enumeration is bounded before identity work");

        string invalidIdentity = "android:" + new String('z', 64);
        string invalidState = Path.Combine(root, "invalid-state"); Directory.CreateDirectory(invalidState);
        File.WriteAllText(Path.Combine(invalidState, "connections.xml"), "<connections><phone identity='" + invalidIdentity + "' serial='" + Usb + "' trusted='true'/></connections>");
        known = new Connections(new AdbBridge(Path.Combine(root, "runtime"), invalidState), invalidState);
        Assert(!known.IsTrusted(invalidIdentity), "malformed stored identity cannot carry a trusted approval");
    }

    private static async Task<Connections> NewAsync(string rememberedSerial, string identity)
    {
        foreach (string path in Directory.GetFiles(scene)) File.Delete(path);
        string state = Path.Combine(root, "state-" + (++sequence));
        AdbBridge bridge = new AdbBridge(Path.Combine(root, "runtime"), state);
        Connections connections = new Connections(bridge, state);
        if (rememberedSerial != null)
        {
            Set("identities", rememberedSerial + "=" + identity + "\n");
            string stable = await connections.IdentityAsync(rememberedSerial, CancellationToken.None);
            connections.Trust(stable, new PhoneDevice { Serial = rememberedSerial, Model = "Fixture", State = "device" });
        }
        Set("commands.log", ""); return connections;
    }
    private static void Set(string name, string text) { File.WriteAllText(Path.Combine(scene, name), text); }
    private static string Log() { return File.ReadAllText(Path.Combine(scene, "commands.log")); }
    private static void Assert(bool condition, string description)
    { if (!condition) throw new Exception("FAIL: " + description); Console.WriteLine("PASS " + (++passed) + ": " + description); }
    private static async Task ExpectAsync<T>(Task task, string message) where T : Exception
    {
        if (await Task.WhenAny(task, Task.Delay(6000)) != task) throw new Exception("Fixture did not settle within six seconds.");
        try { await task; }
        catch (T error) { if (message == null || error.Message.Contains(message)) return; throw new Exception("Unexpected failure: " + error.Message); }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
