using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;

namespace IPMsg.Dock
{
    /// <summary>Materializes an owner-selected phone folder, never a phone path.
    /// A complete bounded ZIP is validated before any child is written. An exact
    /// ownership journal precedes the first write, including cancelled copies.
    /// Cleanup removes those files and empty directories only, never a tree.</summary>
    internal static class FolderSnapshot
    {
        internal const string Mime = "application/vnd.ipmsg.folder+zip";
        internal const int MaximumEntries = 10000, MaximumDepth = 32;
        internal const long MaximumBytes = 32L * 1024 * 1024 * 1024;
        private const long Reserve = 32L * 1024 * 1024;
        private const string Marker = ".ipmsg-folder.xml";
        private const string MarkerOwner = "IPMsgDock.FolderSnapshot";
        private const int MaximumJournalBytes = 8 * 1024 * 1024;

        private sealed class Child
        {
            internal string Relative;
            internal bool Directory;
            internal long Size;
            internal ZipArchiveEntry Archive;
        }

        private sealed class Plan
        {
            internal string Name;
            internal bool Ready;
            internal readonly List<Child> Children = new List<Child>();
        }

        internal static bool IsFolder(BridgeItem item)
        { return item != null && item.Mime == Mime; }
        internal static string DisplayName(BridgeItem item)
        {
            if (item == null) return "";
            string name = item.Name ?? "";
            return IsFolder(item) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        }

        internal static string FolderName(string archiveName)
        {
            if (String.IsNullOrEmpty(archiveName) || !archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new IOException("The shared folder snapshot has no valid name. Select the folder again.");
            string name = archiveName.Substring(0, archiveName.Length - 4);
            RequireLeaf(name);
            return name;
        }

        internal static string Extract(string archivePath, string ownedDirectory, string archiveName,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string wrapper = Local(ownedDirectory);
            RequireSafe(wrapper);
            if (!Directory.Exists(wrapper)) Directory.CreateDirectory(wrapper);
            if (Directory.GetFileSystemEntries(wrapper).Length != 0)
                throw new IOException("The temporary folder is already in use. Select the folder again.");
            Plan plan = new Plan { Name = FolderName(archiveName) };
            string target = Path.Combine(wrapper, plan.Name);
            RequireSafe(target); RequirePath(target);
            bool journal = false, complete = false;
            using (FileStream input = new FileStream(Local(archivePath), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                PreflightArchive(input, token);
                using (ZipArchive archive = new ZipArchive(input, ZipArchiveMode.Read, false, Encoding.UTF8))
                {
                    BuildPlan(plan, archive, target, token);
                    long total = plan.Children.Where(value => !value.Directory).Sum(value => value.Size);
                    CheckSpace(wrapper, total);
                    WriteJournal(wrapper, plan); journal = true;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        Directory.CreateDirectory(target); RequireSafe(target);
                        foreach (Child child in plan.Children.Where(value => value.Directory).OrderBy(value => value.Relative.Length))
                        {
                            token.ThrowIfCancellationRequested();
                            string path = ChildPath(target, child.Relative);
                            RequireSafe(path); Directory.CreateDirectory(path); RequireSafe(path);
                        }
                        byte[] buffer = new byte[81920];
                        foreach (Child child in plan.Children.Where(value => !value.Directory))
                        {
                            token.ThrowIfCancellationRequested();
                            string path = ChildPath(target, child.Relative); RequireSafe(path);
                            using (Stream source = child.Archive.Open())
                            using (FileStream output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                buffer.Length, FileOptions.SequentialScan))
                            {
                                long copied = 0; int count;
                                while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                                {
                                    token.ThrowIfCancellationRequested();
                                    if (count > child.Size - copied) throw new IOException("A folder file exceeded its advertised size.");
                                    output.Write(buffer, 0, count); copied += count;
                                }
                                token.ThrowIfCancellationRequested();
                                output.Flush(true);
                                if (copied != child.Size || output.Length != child.Size)
                                    throw new IOException("A folder file ended before its advertised size. Select the folder again.");
                            }
                        }
                        token.ThrowIfCancellationRequested();
                        plan.Ready = true; WriteJournal(wrapper, plan);
                        complete = true; return target;
                    }
                    finally { if (!complete && journal) DeleteTracked(wrapper, plan); }
                }
            }
        }

        // ZipArchive eagerly allocates its central directory. Bound the raw
        // record count and bytes first, including ZIP64 archives over 4 GB.
        private static void PreflightArchive(FileStream input, CancellationToken token)
        {
            if (input.Length < 22 || input.Length > MaximumBytes) throw InvalidArchive();
            int tailSize = (int)Math.Min(input.Length, 65557L);
            byte[] tail = new byte[tailSize]; input.Position = input.Length - tailSize;
            ReadExact(input, tail, tail.Length); int end = -1;
            for (int i = tail.Length - 22; i >= 0; i--)
                if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { end = i; break; }
            if (end < 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 ||
                U16(tail, end + 8) != U16(tail, end + 10)) throw InvalidArchive();
            ulong count = U16(tail, end + 10), bytes = U32(tail, end + 12), offset = U32(tail, end + 16);
            long boundary = input.Length - tailSize + end;
            if (count == 65535 || bytes == UInt32.MaxValue || offset == UInt32.MaxValue)
            {
                if (boundary < 20) throw InvalidArchive();
                byte[] locator = new byte[20]; input.Position = boundary - 20; ReadExact(input, locator, locator.Length);
                if (U32(locator, 0) != 0x07064b50 || U32(locator, 4) != 0 || U32(locator, 16) != 1) throw InvalidArchive();
                ulong position = U64(locator, 8);
                if (position > (ulong)Math.Max(0, boundary - 76)) throw InvalidArchive();
                byte[] zip64 = new byte[56]; input.Position = (long)position; ReadExact(input, zip64, zip64.Length);
                ulong recordSize = U64(zip64, 4);
                if (U32(zip64, 0) != 0x06064b50 || recordSize < 44 || recordSize > 4096 ||
                    position + 12 + recordSize != (ulong)(boundary - 20) || U32(zip64, 16) != 0 || U32(zip64, 20) != 0 ||
                    U64(zip64, 24) != U64(zip64, 32)) throw InvalidArchive();
                count = U64(zip64, 32); bytes = U64(zip64, 40); offset = U64(zip64, 48); boundary = (long)position;
            }
            if (count > MaximumEntries) throw TooMany();
            if (bytes > 32UL * 1024 * 1024 || offset > (ulong)boundary || bytes != (ulong)boundary - offset) throw InvalidArchive();
            input.Position = (long)offset;
            byte[] header = new byte[46]; ulong consumed = 0, found = 0;
            while (consumed < bytes)
            {
                token.ThrowIfCancellationRequested();
                if (bytes - consumed < (ulong)header.Length || ++found > MaximumEntries) throw InvalidArchive();
                ReadExact(input, header, header.Length);
                int name = U16(header, 28), extra = U16(header, 30), comment = U16(header, 32);
                ulong length = (ulong)(46 + name + extra + comment);
                if (U32(header, 0) != 0x02014b50 || name < 1 || name > 8192 || extra > 4096 || comment > 2048 ||
                    U16(header, 34) != 0 || length > bytes - consumed) throw InvalidArchive();
                consumed += length; input.Seek(name + extra + comment, SeekOrigin.Current);
            }
            if (found != count) throw InvalidArchive();
            input.Position = 0;
        }

        private static void ReadExact(Stream input, byte[] bytes, int length)
        {
            int read = 0;
            while (read < length) { int count = input.Read(bytes, read, length - read); if (count == 0) throw InvalidArchive(); read += count; }
        }
        private static ushort U16(byte[] bytes, int at) { return (ushort)(bytes[at] | bytes[at + 1] << 8); }
        private static uint U32(byte[] bytes, int at) { return (uint)(bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24); }
        private static ulong U64(byte[] bytes, int at) { return U32(bytes, at) | ((ulong)U32(bytes, at + 4) << 32); }

        private static void BuildPlan(Plan plan, ZipArchive archive, string target, CancellationToken token)
        {
            var paths = new Dictionary<string, Child>(StringComparer.OrdinalIgnoreCase);
            var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            if (archive.Entries.Count > MaximumEntries) throw TooMany();
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                string raw = entry.FullName;
                if (String.IsNullOrEmpty(raw) || raw.Length > 2048 || raw.IndexOf('\\') >= 0 || raw[0] == '/') throw UnsafeName();
                bool directory = raw.EndsWith("/", StringComparison.Ordinal);
                string relative = directory ? raw.Substring(0, raw.Length - 1) : raw;
                string[] parts = relative.Split('/');
                if (parts.Length > MaximumDepth || parts.Any(value => String.IsNullOrEmpty(value))) throw UnsafeName();
                foreach (string part in parts) RequireLeaf(part);
                // UNIX symlinks/devices and Windows reparse points are not files.
                int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
                if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                    (unixType != 0 && unixType != 0x8000 && unixType != 0x4000) ||
                    (unixType == 0x4000 && !directory) || (unixType == 0x8000 && directory)) throw UnsafeName();
                if ((directory && entry.Length != 0) || entry.Length < 0 || entry.Length > MaximumBytes ||
                    entry.Length > MaximumBytes - total) throw new IOException("The folder exceeds the 32 GB limit.");
                total += entry.Length;
                if (!explicitNames.Add(relative)) throw Collision();
                string prefix = "";
                for (int i = 0; i < parts.Length; i++)
                {
                    prefix = prefix.Length == 0 ? parts[i] : prefix + "/" + parts[i];
                    bool isDirectory = i < parts.Length - 1 || directory;
                    Child previous;
                    if (paths.TryGetValue(prefix, out previous))
                    {
                        // Case-only alternatives and file/directory aliases must
                        // fail rather than merging or silently renaming data.
                        if (previous.Relative != prefix || !previous.Directory || !isDirectory) throw Collision();
                        if (i == parts.Length - 1) previous.Archive = entry;
                        continue;
                    }
                    if (paths.Count >= MaximumEntries) throw TooMany();
                    Child child = new Child { Relative = prefix, Directory = isDirectory,
                        Size = isDirectory ? 0 : entry.Length, Archive = i == parts.Length - 1 ? entry : null };
                    ChildPath(target, prefix); paths.Add(prefix, child); plan.Children.Add(child);
                }
            }
        }

        /// <summary>Save a ready folder transactionally. No existing destination
        /// is replaced, including a name taken while the copy was in progress.</summary>
        internal static string CopyUnique(string sourceDirectory, string destinationDirectory, CancellationToken token)
        {
            string source = Local(sourceDirectory), destination = Local(destinationDirectory);
            RequireSafe(source); RequireSafe(destination);
            Plan sourcePlan = ReadJournal(Path.GetDirectoryName(source));
            if (sourcePlan == null || !sourcePlan.Ready || sourcePlan.Name != Path.GetFileName(source))
                throw new IOException("The prepared folder is no longer available. Select it again on the phone.");
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("Choose an existing Windows folder.");
            long total = sourcePlan.Children.Where(value => !value.Directory).Sum(value => value.Size);
            CheckSpace(destination, total);
            // Root is unique and stays unpublished until every file is flushed.
            string staging = Path.Combine(destination, ".ipmsg-folder-" + Guid.NewGuid().ToString("N") + ".part");
            RequirePath(staging); RequireSafe(staging);
            if (File.Exists(staging) || Directory.Exists(staging)) throw new IOException("The temporary save folder already exists.");
            Plan copied = new Plan { Name = Path.GetFileName(staging) };
            bool created = false, success = false;
            try
            {
                foreach (Child child in sourcePlan.Children) ChildPath(staging, child.Relative);
                Directory.CreateDirectory(staging); created = true;
                foreach (Child child in sourcePlan.Children.Where(value => value.Directory).OrderBy(value => value.Relative.Length))
                {
                    token.ThrowIfCancellationRequested(); string path = ChildPath(staging, child.Relative);
                    RequireSafe(path); Directory.CreateDirectory(path); RequireSafe(path);
                    copied.Children.Add(child);
                }
                byte[] buffer = new byte[81920];
                foreach (Child child in sourcePlan.Children.Where(value => !value.Directory))
                {
                    token.ThrowIfCancellationRequested();
                    string from = ChildPath(source, child.Relative), to = ChildPath(staging, child.Relative);
                    RequireSafe(from); RequireSafe(to);
                    using (FileStream input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read,
                        buffer.Length, FileOptions.SequentialScan))
                    {
                        if (input.Length != child.Size) throw new IOException("The prepared folder changed. Select it again on the phone.");
                        using (FileStream output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                            buffer.Length, FileOptions.SequentialScan))
                        {
                            copied.Children.Add(child);
                            long written = 0; int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                token.ThrowIfCancellationRequested();
                                if (count > child.Size - written) throw new IOException("The prepared folder changed. Select it again on the phone.");
                                output.Write(buffer, 0, count); written += count;
                            }
                            output.Flush(true);
                            if (written != child.Size || output.Length != child.Size) throw new IOException("A folder file was not completely saved.");
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                for (int suffix = 0; suffix < 10000; suffix++)
                {
                    string name = sourcePlan.Name + (suffix == 0 ? "" : " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")");
                    string candidate = Path.Combine(destination, name); RequirePath(candidate); RequireSafe(candidate);
                    foreach (Child child in sourcePlan.Children) ChildPath(candidate, child.Relative);
                    if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
                    try { Directory.Move(staging, candidate); success = true; return candidate; }
                    catch (IOException) { if (!File.Exists(candidate) && !Directory.Exists(candidate)) throw; }
                }
                throw new IOException("There are too many folders with this name. Choose another destination.");
            }
            finally { if (created && !success) DeleteTracked(destination, copied); }
        }

        internal static void Prune(string ownedDirectory)
        {
            Plan plan = ReadJournal(ownedDirectory);
            if (plan == null) return;
            DeleteTracked(ownedDirectory, plan);
            string target = Path.Combine(ownedDirectory, plan.Name);
            if (Directory.Exists(target) || File.Exists(target)) return;
            RequireSafe(ownedDirectory);
            if (Directory.GetFileSystemEntries(ownedDirectory).Any(value => Path.GetFileName(value) != Marker)) return;
            DeleteFile(ownedDirectory, Path.Combine(ownedDirectory, Marker));
        }

        private static void DeleteTracked(string parent, Plan plan)
        {
            string target = Path.Combine(parent, plan.Name);
            foreach (Child child in plan.Children.Where(value => !value.Directory))
                DeleteFile(parent, ChildPath(target, child.Relative));
            foreach (Child child in plan.Children.Where(value => value.Directory).OrderByDescending(value => value.Relative.Length))
                DeleteEmpty(parent, ChildPath(target, child.Relative));
            DeleteEmpty(parent, target);
        }

        private static void WriteJournal(string wrapper, Plan plan)
        {
            RequireSafe(wrapper);
            string temporary = Path.Combine(wrapper, ".ipmsg-folder-" + Guid.NewGuid().ToString("N") + ".part");
            string marker = Path.Combine(wrapper, Marker); bool created = false;
            try
            {
                RequireSafe(marker);
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = true }))
                    {
                        writer.WriteStartElement("folder"); writer.WriteAttributeString("owner", MarkerOwner);
                        writer.WriteAttributeString("version", "1"); writer.WriteAttributeString("name", plan.Name);
                        writer.WriteAttributeString("ready", plan.Ready ? "1" : "0");
                        foreach (Child child in plan.Children)
                        {
                            writer.WriteStartElement(child.Directory ? "directory" : "file");
                            writer.WriteAttributeString("path", child.Relative);
                            writer.WriteAttributeString("size", child.Size.ToString(CultureInfo.InvariantCulture)); writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }
                }
                if (File.Exists(marker)) File.Replace(temporary, marker, null); else File.Move(temporary, marker);
            }
            finally { if (created) DeleteFile(wrapper, temporary); }
        }

        private static Plan ReadJournal(string wrapper)
        {
            RequireSafe(wrapper); string marker = Path.Combine(wrapper, Marker); RequireSafe(marker);
            if (!File.Exists(marker) || new FileInfo(marker).Length > MaximumJournalBytes) return null;
            using (XmlReader reader = XmlReader.Create(marker, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = MaximumJournalBytes }))
            {
                XmlDocument document = new XmlDocument { XmlResolver = null }; document.Load(reader);
                XmlElement root = document.DocumentElement;
                if (root == null || root.Name != "folder" || root.GetAttribute("owner") != MarkerOwner ||
                    root.GetAttribute("version") != "1" || (root.GetAttribute("ready") != "0" && root.GetAttribute("ready") != "1")) return null;
                string name = root.GetAttribute("name"); RequireLeaf(name);
                Plan plan = new Plan { Name = name, Ready = root.GetAttribute("ready") == "1" };
                string target = Path.Combine(wrapper, name); RequirePath(target);
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
                foreach (XmlNode raw in root.ChildNodes)
                {
                    XmlElement entry = raw as XmlElement; long size;
                    if (entry == null || (entry.Name != "file" && entry.Name != "directory") || plan.Children.Count >= MaximumEntries ||
                        !Int64.TryParse(entry.GetAttribute("size"), NumberStyles.None, CultureInfo.InvariantCulture, out size) ||
                        size < 0 || size > MaximumBytes - total || (entry.Name == "directory" && size != 0)) return null;
                    string relative = entry.GetAttribute("path");
                    ChildPath(target, relative);
                    if (!paths.Add(relative)) return null;
                    plan.Children.Add(new Child { Relative = relative, Directory = entry.Name == "directory", Size = size }); total += size;
                }
                return plan;
            }
        }

        private static string ChildPath(string target, string relative)
        {
            if (String.IsNullOrEmpty(relative) || relative.Length > 2048 || relative.IndexOf('\\') >= 0) throw UnsafeName();
            string[] parts = relative.Split('/');
            if (parts.Length > MaximumDepth) throw UnsafeName();
            foreach (string part in parts) RequireLeaf(part);
            string path = Path.GetFullPath(Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar)));
            Inside(target, path); RequirePath(path); return path;
        }

        private static void RequireLeaf(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 240 || value == "." || value == ".." ||
                value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Any(Char.IsControl) ||
                value.TrimEnd(' ', '.') != value) throw UnsafeName();
            string stem = value.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                ((stem[3] >= '1' && stem[3] <= '9') || "¹²³".IndexOf(stem[3]) >= 0))) throw UnsafeName();
        }
        private static string Local(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Choose a local Windows folder.");
            return full;
        }
        private static void RequirePath(string path)
        {
            if (path.Length > 240) throw new IOException("This folder tree is too deep for Windows. Choose a shorter folder name or destination.");
        }
        internal static void RequireSafe(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { attributes = 0; }
                catch (DirectoryNotFoundException) { attributes = 0; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Folder links and reparse points cannot be shared.");
                string parent = Path.GetDirectoryName(current); if (parent == current) break; current = parent;
            }
        }
        private static void Inside(string root, string path)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw UnsafeName();
        }
        private static void CheckSpace(string directory, long bytes)
        {
            DriveInfo disk = new DriveInfo(Path.GetPathRoot(directory));
            if (bytes > Math.Max(0, disk.AvailableFreeSpace - Reserve)) throw new IOException("Windows needs more space for this folder.");
        }
        private static void DeleteFile(string root, string path)
        {
            try { Inside(root, path); RequireSafe(path); if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        private static void DeleteEmpty(string root, string path)
        {
            try { Inside(root, path); RequireSafe(path); if (Directory.Exists(path)) Directory.Delete(path, false); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        private static IOException UnsafeName()
        { return new IOException("This folder contains a name or link Windows cannot safely save. Rename it on the phone and select the folder again."); }
        private static IOException Collision()
        { return new IOException("This folder has duplicate or case-only file names. Rename them on the phone before sharing."); }
        private static IOException TooMany() { return new IOException("Select a smaller folder (up to 10,000 files and folders)."); }
        private static IOException InvalidArchive() { return new IOException("The folder snapshot is invalid or too large. Select the folder again on the phone."); }
    }
}
