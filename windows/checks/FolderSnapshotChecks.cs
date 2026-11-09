using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using IPMsg.Dock;

internal static class FolderSnapshotChecks
{
    private static string root;
    private static int passed, serial;

    [STAThread]
    public static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "ipmsg-folder-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            NestedAndEmpty(); RejectedArchives(); CentralDirectoryPreflight(); SafeSave(); CacheCleanup();
            Console.WriteLine("RESULT: " + passed + " folder checks passed. Generated local ZIPs and CF_HDROP data only; no phone or Explorer gesture used.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string absolute = Path.GetFullPath(root), temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
            if (absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("ipmsg-folder-check-", StringComparison.Ordinal))
                try { Directory.Delete(absolute, true); } catch (IOException) { }
        }
    }

    private static void NestedAndEmpty()
    {
        string archive = Zip(new[] { E("empty/", ""), E("photos/caf\u00e9 \ud83d\udcf7.txt", "\u0645\u0631\u062d\u0628\u0627"), E("zero.bin", "") });
        string wrapper = Next("owned"), target = FolderSnapshot.Extract(archive, wrapper, "Phone files.zip", CancellationToken.None);
        Check(Path.GetFileName(target) == "Phone files" && Directory.Exists(Path.Combine(target, "empty")), "Empty directories and the original folder name survive extraction");
        Check(File.ReadAllText(Path.Combine(target, "photos", "caf\u00e9 \ud83d\udcf7.txt"), Encoding.UTF8) == "\u0645\u0631\u062d\u0628\u0627" && new FileInfo(Path.Combine(target, "zero.bin")).Length == 0,
            "Unicode nested names/content and zero-byte files survive extraction");
        DataObject drop = VirtualFileDrag.CreateLocalDataObject(new[] { target });
        string[] paths = (string[])drop.GetData(DataFormats.FileDrop);
        Check(paths.Length == 1 && paths[0] == target && Directory.Exists(paths[0]), "Native CF_HDROP advertises the actual ready directory rather than an archive");
        FolderSnapshot.Prune(wrapper);
        Check(Directory.Exists(wrapper) && Directory.GetFileSystemEntries(wrapper).Length == 0, "The exact ownership journal cleans nested files and empty directories");
        string empty = FolderSnapshot.Extract(Zip(new KeyValuePair<string, string>[0]), Next("empty"), "Empty.zip", CancellationToken.None);
        Check(Directory.Exists(empty) && Directory.GetFileSystemEntries(empty).Length == 0, "An entirely empty selected folder is a real directory");
        Check(FolderSnapshot.IsFolder(new BridgeItem { Name = "Data.zip", Mime = FolderSnapshot.Mime }) &&
            FolderSnapshot.DisplayName(new BridgeItem { Name = "Data.zip.zip", Mime = FolderSnapshot.Mime }) == "Data.zip" &&
            !FolderSnapshot.IsFolder(new BridgeItem { Name = "Data.zip", Mime = "application/zip" }), "Only the explicit folder MIME is materialized; ordinary ZIP files stay files");
    }

    private static void RejectedArchives()
    {
        foreach (string bad in new[] { "../outside.txt", "/root.txt", "C:/drive.txt", "a\\back.txt", "a:stream", "NUL.txt", "dot./file", "space /file", "a//b", "./file" })
        {
            string wrapper = Next("reject");
            Reject<IOException>(delegate { FolderSnapshot.Extract(Zip(new[] { E(bad, "bad") }), wrapper, "Unsafe.zip", CancellationToken.None); });
            Check(!Directory.Exists(wrapper) || Directory.GetFileSystemEntries(wrapper).Length == 0, "Unsafe ZIP path is rejected before files are created: " + bad);
        }
        RejectArchive(new[] { E("A/file.txt", "one"), E("a/other.txt", "two") }, "Case-only parent names are rejected");
        RejectArchive(new[] { E("same.txt", "one"), E("same.txt", "two") }, "Duplicate entry names are rejected without overwriting");
        RejectArchive(new[] { E("a", "one"), E("a/file", "two") }, "File/directory collisions are rejected");
        RejectArchive(new[] { E("bad/", "data") }, "A directory containing payload bytes is rejected");
        RejectArchive(new[] { E(String.Join("/", Enumerable.Repeat("d", 33)), "data") }, "Excessive nesting is rejected");
        string symlink = Zip(new[] { E("link", "target") }, delegate(ZipArchiveEntry entry) { entry.ExternalAttributes = unchecked((int)0xa1ff0000); });
        Reject<IOException>(delegate { FolderSnapshot.Extract(symlink, Next("link"), "Links.zip", CancellationToken.None); });
        Check(true, "UNIX symlink entries cannot become Windows files");
        string reparse = Zip(new[] { E("link", "target") }, delegate(ZipArchiveEntry entry) { entry.ExternalAttributes = (int)FileAttributes.ReparsePoint; });
        Reject<IOException>(delegate { FolderSnapshot.Extract(reparse, Next("reparse"), "Links.zip", CancellationToken.None); });
        Check(true, "Windows reparse attributes are rejected");
        string excessive = Zip(Enumerable.Range(0, 10001).Select(value => E("f" + value, "")));
        Reject<IOException>(delegate { FolderSnapshot.Extract(excessive, Next("many"), "Many.zip", CancellationToken.None); });
        Check(true, "The entry limit rejects oversized manifests before creating output files");
        string longName = String.Join("/", Enumerable.Repeat("directory", 20)) + "/file.txt";
        RejectArchive(new[] { E(longName, "data") }, "Unrepresentable Windows source paths fail clearly before partial extraction");
        using (CancellationTokenSource cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); string wrapper = Next("cancel");
            Reject<OperationCanceledException>(delegate { FolderSnapshot.Extract(Zip(new[] { E("a", "data") }), wrapper, "Cancel.zip", cancel.Token); });
            Check(!Directory.Exists(wrapper), "Cancellation before extraction creates no folder bytes");
        }
        string existing = Next("occupied"); Directory.CreateDirectory(existing); File.WriteAllText(Path.Combine(existing, "keep.txt"), "keep");
        Reject<IOException>(delegate { FolderSnapshot.Extract(Zip(new[] { E("a", "data") }), existing, "Occupied.zip", CancellationToken.None); });
        Check(File.ReadAllText(Path.Combine(existing, "keep.txt")) == "keep", "An existing extraction wrapper is preserved rather than reused or overwritten");
    }

    private static void SafeSave()
    {
        string source = FolderSnapshot.Extract(Zip(new[] { E("empty/", ""), E("nested/a.txt", "alpha"), E("z.txt", "zulu") }), Next("source"), "Album.zip", CancellationToken.None);
        string destination = Next("destination"); Directory.CreateDirectory(destination);
        Directory.CreateDirectory(Path.Combine(destination, "Album")); File.WriteAllText(Path.Combine(destination, "Album", "keep.txt"), "keep");
        string saved = FolderSnapshot.CopyUnique(source, destination, CancellationToken.None);
        Check(Path.GetFileName(saved) == "Album (1)" && File.ReadAllText(Path.Combine(saved, "nested", "a.txt")) == "alpha" && Directory.Exists(Path.Combine(saved, "empty")),
            "Save publishes a complete actual folder with a unique destination name");
        Check(File.ReadAllText(Path.Combine(destination, "Album", "keep.txt")) == "keep" && !Directory.GetDirectories(destination, "*.part").Any(),
            "Saving never overwrites an existing folder and leaves no completed staging tree");
        File.Delete(Path.Combine(source, "z.txt"));
        Reject<IOException>(delegate { FolderSnapshot.CopyUnique(source, destination, CancellationToken.None); });
        Check(!Directory.GetDirectories(destination, "*.part").Any() && !Directory.Exists(Path.Combine(destination, "Album (2)")),
            "A changed or interrupted source rolls back exactly the partial destination rather than reporting saved");
        string ordinary = Next("ordinary"); Directory.CreateDirectory(ordinary); File.WriteAllText(Path.Combine(ordinary, "x"), "x");
        Reject<IOException>(delegate { FolderSnapshot.CopyUnique(ordinary, destination, CancellationToken.None); });
        Check(true, "Save refuses an unjournaled arbitrary source directory");
    }

    private static void CentralDirectoryPreflight()
    {
        string actual = Zip(new[] { E("a", "a"), E("b", "b") });
        byte[] bytes = File.ReadAllBytes(actual); int end = bytes.Length - 22;
        bytes[end + 8] = bytes[end + 10] = 1;
        File.WriteAllBytes(actual, bytes);
        Reject<IOException>(delegate { FolderSnapshot.Extract(actual, Next("wrong-count"), "Wrong.zip", CancellationToken.None); });
        Check(true, "A central directory whose actual records disagree with EOCD is rejected before ZipArchive allocation");
        string inflated = Zip(new[] { E("a", "a") }); bytes = File.ReadAllBytes(inflated); end = bytes.Length - 22;
        bytes[end + 15] = 127; File.WriteAllBytes(inflated, bytes);
        Reject<IOException>(delegate { FolderSnapshot.Extract(inflated, Next("inflated-directory"), "Wrong.zip", CancellationToken.None); });
        Check(true, "An oversized claimed central directory is rejected without allocating it");
        string split = Zip(new[] { E("a", "a") }); bytes = File.ReadAllBytes(split); end = bytes.Length - 22;
        bytes[end + 4] = 1; File.WriteAllBytes(split, bytes);
        Reject<IOException>(delegate { FolderSnapshot.Extract(split, Next("spanned"), "Wrong.zip", CancellationToken.None); });
        Check(true, "Spanned or multi-disk archives are rejected");
        string zip64 = Zip(new[] { E("big-compatible.txt", "zip64") }); bytes = File.ReadAllBytes(zip64); end = bytes.Length - 22;
        uint directoryBytes = BitConverter.ToUInt32(bytes, end + 12), directoryOffset = BitConverter.ToUInt32(bytes, end + 16);
        ushort count = BitConverter.ToUInt16(bytes, end + 10);
        using (FileStream output = new FileStream(zip64, FileMode.Create, FileAccess.Write))
        using (BinaryWriter writer = new BinaryWriter(output))
        {
            writer.Write(bytes, 0, end); long position = output.Position;
            writer.Write(0x06064b50U); writer.Write(44UL); writer.Write((ushort)45); writer.Write((ushort)45);
            writer.Write(0U); writer.Write(0U); writer.Write((ulong)count); writer.Write((ulong)count);
            writer.Write((ulong)directoryBytes); writer.Write((ulong)directoryOffset);
            writer.Write(0x07064b50U); writer.Write(0U); writer.Write((ulong)position); writer.Write(1U);
            writer.Write(0x06054b50U); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write(UInt16.MaxValue); writer.Write(UInt16.MaxValue); writer.Write(UInt32.MaxValue); writer.Write(UInt32.MaxValue); writer.Write((ushort)0);
        }
        string extracted = FolderSnapshot.Extract(zip64, Next("zip64"), "Compatible.zip", CancellationToken.None);
        Check(File.ReadAllText(Path.Combine(extracted, "big-compatible.txt")) == "zip64", "Bounded ZIP64 central directories remain compatible with large-folder snapshots");
    }

    private static void CacheCleanup()
    {
        DateTime now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        string state = Next("state"), sessionPath, folder;
        using (TemporaryBridgeCache cache = TemporaryBridgeCache.CreateSession(state, delegate { return now; }))
        {
            sessionPath = cache.DirectoryPath;
            string wrapper = cache.CreateFolderDirectory("Shared.zip");
            folder = FolderSnapshot.Extract(Zip(new[] { E("nested/a.txt", "a"), E("empty/", "") }), wrapper, "Shared.zip", CancellationToken.None);
            TemporaryBridgeCache.Prune(state, now.AddDays(1));
            Check(Directory.Exists(folder), "An active cache session retains a folder even after the nominal grace period");
            File.WriteAllText(Path.Combine(folder, "unknown.txt"), "keep");
        }
        TemporaryBridgeCache.Prune(state, now.AddHours(2));
        Check(File.Exists(Path.Combine(folder, "unknown.txt")) && !File.Exists(Path.Combine(folder, "nested", "a.txt")) && Directory.Exists(sessionPath),
            "Retired pruning removes only journaled files while preserving unknown data and its session marker");
        File.Delete(Path.Combine(folder, "unknown.txt"));
        TemporaryBridgeCache.Prune(state, now.AddHours(3));
        Check(!Directory.Exists(sessionPath), "A later prune finishes the exact folder/session cleanup after the unknown file is removed");
        string malformed = Next("malformed"), target = FolderSnapshot.Extract(Zip(new[] { E("keep.txt", "keep") }), malformed, "Safe.zip", CancellationToken.None);
        string marker = Path.Combine(malformed, ".ipmsg-folder.xml");
        File.WriteAllText(marker, File.ReadAllText(marker).Replace("keep.txt", "../escape.txt"));
        Reject<IOException>(delegate { FolderSnapshot.Prune(malformed); });
        Check(File.Exists(Path.Combine(target, "keep.txt")), "A tampered path in an ownership journal cannot authorize deletion");
    }

    private static KeyValuePair<string, string> E(string name, string content) { return new KeyValuePair<string, string>(name, content); }
    private static string Zip(IEnumerable<KeyValuePair<string, string>> entries) { return Zip(entries, null); }
    private static string Zip(IEnumerable<KeyValuePair<string, string>> entries, Action<ZipArchiveEntry> configure)
    {
        string path = Next("archive") + ".zip";
        using (FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Create, false, Encoding.UTF8))
            foreach (var item in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(item.Key, CompressionLevel.Fastest);
                if (configure != null) configure(entry);
                using (Stream output = entry.Open()) { byte[] bytes = Encoding.UTF8.GetBytes(item.Value); output.Write(bytes, 0, bytes.Length); }
            }
        return path;
    }
    private static void RejectArchive(IEnumerable<KeyValuePair<string, string>> entries, string reason)
    {
        string wrapper = Next("reject");
        Reject<IOException>(delegate { FolderSnapshot.Extract(Zip(entries), wrapper, "Rejected.zip", CancellationToken.None); });
        Check(!Directory.Exists(wrapper) || Directory.GetFileSystemEntries(wrapper).Length == 0, reason);
    }
    private static string Next(string prefix) { return Path.Combine(root, prefix + (++serial)); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); passed++; Console.WriteLine("PASS: " + reason); }
}
