using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using IPMsg.Dock;

internal static class TemporaryBridgeCacheChecks
{
    private static int passed;
    private static string fixture;
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--abandon")
        {
            var child = TemporaryBridgeCache.CreateSession(args[1]);
            Console.WriteLine(Download(child, "Abandoned.txt")); Console.Out.Flush();
            Console.ReadLine(); // Leave without Retire, as an interrupted process would.
            return 0;
        }
        fixture = Path.Combine(Path.GetTempPath(), "ipmsg-tc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(fixture);
        try
        {
            ExpiryAndUse(); NonblockingUse(); AbandonedProcess(); UnknownAndLocked(); MarkerTraversal(); JunctionSafety(); HardLinkSafety(); LegacySafety();
            Console.WriteLine("RESULT: " + passed + " temporary-cache safety checks passed. Isolated fixture files only; no phone, UI, drag or live state accessed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { RemoveFixture(fixture); }
    }

    private static void NonblockingUse()
    {
        string state = State("nonblocking"); DateTime now = Utc();
        var cache = TemporaryBridgeCache.CreateSession(state, delegate { return now; });
        string marker = Path.Combine(cache.DirectoryPath, ".ipmsg-bridge-session.xml");
        DateTime stamp = File.GetLastWriteTimeUtc(marker); IDisposable use;
        bool acquired = cache.TryBeginUse(out use), untouched = File.GetLastWriteTimeUtc(marker) == stamp;
        if (use != null) use.Dispose(); cache.Retire();
        bool refused = !cache.TryBeginUse(out use) && use == null;
        Check(acquired && untouched && refused, "TryBeginUse acquires an active lease without writing a marker and rejects retired sessions");

        cache = TemporaryBridgeCache.CreateSession(state, delegate { return now; });
        object gate = typeof(TemporaryBridgeCache).GetField("gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(cache);
        using (var held = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var holder = new Thread(delegate() { lock (gate) { held.Set(); release.Wait(); } }); holder.Start();
            if (!held.Wait(5000)) throw new Exception("The isolated contention fixture did not start.");
            IDisposable denied = null;
            Task<bool> attempt = Task.Run(delegate { return cache.TryBeginUse(out denied); });
            bool immediate;
            try { immediate = attempt.Wait(1000); }
            finally { release.Set(); holder.Join(); }
            Check(immediate && !attempt.GetAwaiter().GetResult() && denied == null,
                "TryBeginUse refuses a contended cache gate immediately instead of waiting for preparation disk I/O");
        }
        cache.Retire();
    }

    private static void AbandonedProcess()
    {
        string state = State("abandoned"), path;
        var info = new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,
            "--abandon \"" + state + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        DateTime now = DateTime.UtcNow.AddMinutes(1);
        using (Process process = Process.Start(info))
        {
            path = process.StandardOutput.ReadLine();
            if (String.IsNullOrEmpty(path) || !Path.GetFullPath(path).StartsWith(Path.GetFullPath(state) + "\\", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Invalid isolated cache child response.");
            TemporaryBridgeCache.Prune(state, now.AddDays(30));
            Check(File.Exists(path), "An active owner in another process is protected by the Windows file lock");
            process.StandardInput.WriteLine("finish"); process.StandardInput.Flush();
            if (!process.WaitForExit(5000) || process.ExitCode != 0) throw new Exception("Isolated cache child did not finish.");
        }
        TemporaryBridgeCache.Prune(state, now);
        Check(File.Exists(path), "An abandoned session is first retired with a fresh grace period");
        TemporaryBridgeCache.Prune(state, now.AddMinutes(59));
        Check(File.Exists(path), "An interrupted owner cannot cause immediate cleanup on the next launch");
        TemporaryBridgeCache.Prune(state, now.AddHours(1));
        Check(!File.Exists(path), "An abandoned process's expired sources are reclaimed on a later prune");
    }

    private static void ExpiryAndUse()
    {
        string state = State("expiry"); DateTime now = Utc();
        var cache = TemporaryBridgeCache.CreateSession(state, delegate { return now; });
        string path = Download(cache, "File.txt"), directory = cache.DirectoryPath;
        now = now.AddDays(2); TemporaryBridgeCache.Prune(state, now);
        Check(File.Exists(path), "Active sessions are protected even after the grace interval");
        IDisposable work = cache.BeginUse(); cache.Retire();
        now = now.AddHours(2); TemporaryBridgeCache.Prune(state, now);
        Check(File.Exists(path), "An in-flight preparation or native handoff stays protected after retirement");
        work.Dispose();
        now = now.AddMinutes(59); TemporaryBridgeCache.Prune(state, now);
        Check(File.Exists(path), "Finishing the final use starts a complete one-hour grace period");
        now = now.AddMinutes(1); TemporaryBridgeCache.Prune(state, now);
        Check(!Directory.Exists(directory), "An expired retired session is removed while the app can still run");

        cache = TemporaryBridgeCache.CreateSession(state, delegate { return now; });
        directory = cache.DirectoryPath; path = Download(cache, "Second.txt"); cache.Retire();
        now = now.AddMinutes(40); cache.Touch();
        now = now.AddMinutes(30); TemporaryBridgeCache.Prune(state, now);
        Check(File.Exists(path), "A recent source handoff extends retention");
        now = now.AddMinutes(30); TemporaryBridgeCache.Prune(state, now);
        Check(!Directory.Exists(directory), "A later startup prune also clears the retired source");

        cache = TemporaryBridgeCache.CreateSession(state, delegate { return now; });
        path = Download(cache, new String('L', 140) + "😀.jpg"); directory = cache.DirectoryPath;
        string partialDirectory = Path.Combine(cache.CreateDownloadDirectory("Interrupted.txt"), "bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(partialDirectory); string partial = Path.Combine(partialDirectory, ".download.part"); File.WriteAllText(partial, "incomplete");
        Check(path.Length <= 240 && File.Exists(path), "A long selected name uses the same safe Windows path budget as the downloader");
        cache.Retire(); TemporaryBridgeCache.Prune(state, now.AddHours(1));
        Check(!Directory.Exists(directory) && !File.Exists(partial), "Long-name sources and registered incomplete downloads are reclaimed after grace");
    }

    private static void UnknownAndLocked()
    {
        string state = State("unknown"); DateTime now = Utc();
        File.WriteAllText(Path.Combine(state, "settings.xml"), "unrelated state");
        var cache = TemporaryBridgeCache.CreateSession(state, delegate { return now; });
        string path = Download(cache, "Expected.txt"), directory = cache.DirectoryPath;
        string unknown = Path.Combine(Path.GetDirectoryName(path), "Do not delete.txt");
        File.WriteAllText(unknown, "untracked"); cache.Retire(); now = now.AddHours(2);
        using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            TemporaryBridgeCache.Prune(state, now);
            Check(File.Exists(path) && File.Exists(unknown), "Locked sources and unknown files survive a partial prune");
        }
        TemporaryBridgeCache.Prune(state, now);
        Check(!File.Exists(path) && File.ReadAllText(unknown) == "untracked" && Directory.Exists(directory),
            "A later prune retries the locked source and preserves untracked content");
        File.Delete(unknown); TemporaryBridgeCache.Prune(state, now);
        Check(!Directory.Exists(directory) && File.ReadAllText(Path.Combine(state, "settings.xml")) == "unrelated state",
            "Empty tracked directories are retried without reading or removing other state keys");
        string unowned = Path.Combine(state, "bridge-cache", "not-a-session");
        Directory.CreateDirectory(unowned); File.WriteAllText(Path.Combine(unowned, "keep.txt"), "keep");
        string unmarked = Path.Combine(state, "bridge-cache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unmarked); File.WriteAllText(Path.Combine(unmarked, "keep.txt"), "keep");
        TemporaryBridgeCache.Prune(state, now.AddDays(30));
        Check(File.Exists(Path.Combine(unowned, "keep.txt")) && File.Exists(Path.Combine(unmarked, "keep.txt")),
            "Unknown directories and unmarked arbitrary GUID directories are not claimed");
    }

    private static void MarkerTraversal()
    {
        string state = State("traversal"); DateTime now = Utc();
        string destination = Path.Combine(state, "Windows destination.txt"); File.WriteAllText(destination, "saved");
        var cache = TemporaryBridgeCache.CreateSession(state, delegate { return now; });
        string source = Download(cache, "Expected.txt"), directory = cache.DirectoryPath; cache.Retire();
        string marker = Path.Combine(directory, ".ipmsg-bridge-session.xml");
        var xml = new XmlDocument(); xml.Load(marker);
        XmlElement malicious = xml.CreateElement("file"); malicious.SetAttribute("path", "..\\..\\Windows destination.txt");
        xml.DocumentElement.AppendChild(malicious); xml.Save(marker);
        TemporaryBridgeCache.Prune(state, now.AddDays(2));
        Check(File.ReadAllText(destination) == "saved" && File.Exists(source), "Traversal in an ownership marker invalidates it without deleting any source or destination");

        xml.Load(marker); ((XmlElement)xml.DocumentElement.LastChild).SetAttribute("path", destination); xml.Save(marker);
        TemporaryBridgeCache.Prune(state, now.AddDays(2));
        Check(File.Exists(destination) && File.Exists(source), "Absolute destination paths cannot be accepted as owned files");
    }

    private static void JunctionSafety()
    {
        string state = State("junction"); DateTime now = Utc();
        var cache = TemporaryBridgeCache.CreateSession(state, delegate { return now; });
        string wrapper = cache.CreateDownloadDirectory("Expected.txt"); cache.Retire();
        string outside = Path.Combine(fixture, "junction-target"); Directory.CreateDirectory(outside);
        string target = Path.Combine(outside, "Expected.txt"); File.WriteAllText(target, "destination");
        string link = Path.Combine(wrapper, "bridge-" + Guid.NewGuid().ToString("N"));
        // All interpolated values are newly allocated fixture GUID paths. The
        // target is verified inside the fixture; this helper only creates a link.
        var info = new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + outside + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (Process process = Process.Start(info))
        { string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit(); if (process.ExitCode != 0) throw new Exception("Could not create isolated junction fixture: " + output); }
        try
        {
            TemporaryBridgeCache.Prune(state, now.AddDays(2));
            Check(File.ReadAllText(target) == "destination" && Directory.Exists(link), "Pruning never traverses a junction in an owned source tree");
            bool refused = false;
            try { TemporaryBridgeCache.CreateSession(link); } catch (IOException) { refused = true; }
            Check(refused, "Session creation refuses a reparse-point state ancestor");
        }
        finally { Directory.Delete(link, false); }
        TemporaryBridgeCache.Prune(state, now.AddDays(2));
        Check(!Directory.Exists(cache.DirectoryPath), "Removing a foreign junction lets a later prune finish safely");
    }

    private static void LegacySafety()
    {
        string state = State("legacy"); DateTime now = Utc();
        string session = Path.Combine(state, "bridge-cache", Guid.NewGuid().ToString("N"));
        string bridge = Path.Combine(session, "bridge-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(bridge);
        string source = Path.Combine(bridge, "Legacy.txt"); File.WriteAllText(source, "legacy");
        string leases = Path.Combine(session, "leases", new String('a', 64)); Directory.CreateDirectory(leases);
        string lease = Path.Combine(leases, new String('1', 32) + "-6-1700000000000.xml");
        using (XmlWriter writer = XmlWriter.Create(lease))
        { writer.WriteStartElement("ready"); writer.WriteAttributeString("path", source); writer.WriteAttributeString("stamp", File.GetLastWriteTimeUtc(source).Ticks.ToString()); writer.WriteEndElement(); }
        TemporaryBridgeCache.Prune(state, now);
        Check(File.Exists(source) && File.Exists(Path.Combine(session, ".ipmsg-bridge-session.xml")), "A wholly recognized legacy cache is marked and receives a fresh grace period");
        TemporaryBridgeCache.Prune(state, now.AddMinutes(59)); Check(File.Exists(source), "Legacy migration never deletes sources immediately");
        TemporaryBridgeCache.Prune(state, now.AddHours(1)); Check(!Directory.Exists(session), "An expired recognized legacy cache is reclaimed");

        session = Path.Combine(state, "bridge-cache", Guid.NewGuid().ToString("N"));
        bridge = Path.Combine(session, "bridge-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(bridge);
        source = Path.Combine(bridge, "Unverifiable.txt"); File.WriteAllText(source, "unknown");
        TemporaryBridgeCache.Prune(state, now.AddDays(100));
        Check(File.Exists(source) && !File.Exists(Path.Combine(session, ".ipmsg-bridge-session.xml")), "Legacy-shaped files without matching leases remain unclaimed");
    }

    private static void HardLinkSafety()
    {
        string state = State("hardlink"); DateTime now = Utc();
        var cache = TemporaryBridgeCache.CreateSession(state, delegate { return now; });
        string source = Download(cache, "Expected.txt"), directory = cache.DirectoryPath;
        string destination = Path.Combine(state, "Saved Windows file.txt"); File.WriteAllText(destination, "saved destination bytes");
        string fixedPending = Path.Combine(directory, ".ipmsg-bridge-session.part");
        if (!CreateHardLink(fixedPending, destination, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        now = now.AddMinutes(1); cache.Touch();
        Check(File.ReadAllText(destination) == "saved destination bytes" && File.ReadAllText(fixedPending) == "saved destination bytes",
            "Saving a marker never truncates a preexisting pending-name hard link or its outside target");

        string marker = Path.Combine(directory, ".ipmsg-bridge-session.xml");
        string externalMarker = Path.Combine(state, "Unowned marker copy.xml"); File.Copy(marker, externalMarker);
        string externalText = File.ReadAllText(externalMarker); File.Delete(marker);
        if (!CreateHardLink(marker, externalMarker, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        now = now.AddMinutes(1); cache.Touch();
        Check(File.ReadAllText(externalMarker) == externalText && File.ReadAllText(marker) != externalText,
            "Atomic replacement of an existing marker hard link changes only the cache directory entry");

        string stale = Path.Combine(directory, ".ipmsg-bridge-session-" + Guid.NewGuid().ToString("N") + ".part"); File.Copy(marker, stale);
        string unknown = Path.Combine(directory, ".ipmsg-bridge-session-" + Guid.NewGuid().ToString("N") + ".part"); File.WriteAllText(unknown, "unowned pending content");
        cache.Retire(); TemporaryBridgeCache.Prune(state, now.AddHours(1));
        Check(!File.Exists(source) && !File.Exists(stale) && File.Exists(fixedPending) && File.Exists(unknown) &&
            File.ReadAllText(destination) == "saved destination bytes" && File.ReadAllText(externalMarker) == externalText,
            "Pruning recognizes owned crash-left markers, preserves unknown pending files, and leaves hard-link destinations intact");
        File.Delete(fixedPending); File.Delete(unknown); TemporaryBridgeCache.Prune(state, now.AddHours(1));
        Check(!Directory.Exists(directory) && File.ReadAllText(destination) == "saved destination bytes" && File.ReadAllText(externalMarker) == externalText,
            "A later prune removes the remaining owned session while outside hard-link targets survive");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);

    private static string Download(TemporaryBridgeCache cache, string name)
    {
        string bridge = Path.Combine(cache.CreateDownloadDirectory(name), "bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bridge); string file = Path.Combine(bridge, VirtualFileDrag.ExportNameForDirectory(name, bridge)); File.WriteAllText(file, "source"); return file;
    }
    private static string State(string name) { string path = Path.Combine(fixture, name); Directory.CreateDirectory(path); return path; }
    private static DateTime Utc() { return new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc); }
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS: " + name); }
    private static void RemoveFixture(string path)
    {
        string absolute = Path.GetFullPath(path), temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
        if (!absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("ipmsg-tc-")) return;
        // Explicit enumeration also keeps test cleanup from following a link if
        // a fixture fails before its local junction cleanup completes.
        foreach (string file in Directory.GetFiles(path)) try { File.Delete(file); } catch (IOException) { }
        foreach (string child in Directory.GetDirectories(path))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) Directory.Delete(child, false);
            else RemoveFixtureChild(child, absolute);
        }
        try { Directory.Delete(path, false); } catch (IOException) { }
    }
    private static void RemoveFixtureChild(string path, string ownedRoot)
    {
        if (!Path.GetFullPath(path).StartsWith(ownedRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new SecurityException("Fixture cleanup escaped its root.");
        foreach (string file in Directory.GetFiles(path)) try { File.Delete(file); } catch (IOException) { }
        foreach (string child in Directory.GetDirectories(path))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) Directory.Delete(child, false);
            else RemoveFixtureChild(child, ownedRoot);
        }
        try { Directory.Delete(path, false); } catch (IOException) { }
    }
}
