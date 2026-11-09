using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace IPMsg.Dock
{
    /// <summary>
    /// Owns only bridge download sources, never a Windows destination. Retired
    /// sources have an hour's grace for drop targets; pruning runs while the
    /// dock is running or on a later launch, not after uninstall or shutdown.
    /// </summary>
    public sealed class TemporaryBridgeCache : IDisposable
    {
        public static readonly TimeSpan GracePeriod = TimeSpan.FromHours(1);
        private const string Marker = ".ipmsg-bridge-session.xml";
        private const string PendingMarker = ".ipmsg-bridge-session.part";
        private const string PendingPrefix = ".ipmsg-bridge-session-";
        private const string Active = ".ipmsg-bridge-active";
        private const string Owner = "IPMsgDock.TemporaryBridgeCache";
        private const int MaxEntries = 1000;
        private readonly object gate = new object();
        private readonly Func<DateTime> utcNow;
        private readonly string cacheRoot;
        private readonly Session session;
        private FileStream active;
        private int uses;
        private bool retired;
        public string DirectoryPath { get; private set; }

        private TemporaryBridgeCache(string stateDirectory, Func<DateTime> clock)
        {
            utcNow = clock;
            cacheRoot = CacheRoot(stateDirectory);
            EnsureDirectory(cacheRoot);
            DirectoryPath = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));
            if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath)) throw new IOException("The temporary session already exists.");
            Directory.CreateDirectory(DirectoryPath);
            session = new Session { Id = Path.GetFileName(DirectoryPath), Created = Now(), Touched = Now() };
            try
            {
                active = new FileStream(Path.Combine(DirectoryPath, Active), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                Save(DirectoryPath, session);
            }
            catch
            {
                if (active != null) active.Dispose();
                throw;
            }
        }

        public static TemporaryBridgeCache CreateSession(string stateDirectory)
        { return new TemporaryBridgeCache(stateDirectory, delegate { return DateTime.UtcNow; }); }

        internal static TemporaryBridgeCache CreateSession(string stateDirectory, Func<DateTime> clock)
        { return new TemporaryBridgeCache(stateDirectory, clock); }

        /// <summary>Call around a preparation or a native handoff. Retirement
        /// cannot release the active lock while that operation is still alive.</summary>
        public IDisposable BeginUse()
        {
            lock (gate)
            {
                if (retired) throw new ObjectDisposedException("TemporaryBridgeCache");
                uses++;
                return new Use(this);
            }
        }

        public bool TryBeginUse(out IDisposable use)
        {
            use = null;
            if (!System.Threading.Monitor.TryEnter(gate, 0)) return false;
            try
            {
                if (retired) return false;
                uses++;
                use = new Use(this);
                return true;
            }
            finally { System.Threading.Monitor.Exit(gate); }
        }

        /// <summary>Register the exact exported name before the downloader can
        /// create bytes. The downloader owns its bridge-GUID child directory.</summary>
        public string CreateDownloadDirectory(string selectedName)
        {
            lock (gate)
            {
                if (retired) throw new ObjectDisposedException("TemporaryBridgeCache");
                RequireSafe(DirectoryPath);
                if (session.Downloads.Count + session.Folders.Count >= MaxEntries) throw new IOException("End file sharing before preparing more files.");
                string directory = "download-" + Guid.NewGuid().ToString("N");
                string path = Path.Combine(DirectoryPath, directory);
                // DownloadAsync chooses the GUID later, but its fixed-width
                // directory has exactly the same path budget as this placeholder.
                string name = VirtualFileDrag.ExportNameForDirectory(selectedName,
                    Path.Combine(path, "bridge-" + new String('0', 32)));
                if (!Leaf(name) || name == ".download.part") throw new IOException("The file name cannot be used for a temporary download.");
                session.Downloads.Add(new Download { Directory = directory, Name = name });
                session.Touched = Math.Max(session.Touched, Now());
                Save(DirectoryPath, session);
                Directory.CreateDirectory(path);
                RequireSafe(path);
                return path;
            }
        }

        /// <summary>A folder gets a shorter direct wrapper and its own exact
        /// entry journal; the existing single-file ownership rules stay intact.</summary>
        public string CreateFolderDirectory(string selectedName)
        {
            FolderSnapshot.FolderName(selectedName);
            lock (gate)
            {
                if (retired) throw new ObjectDisposedException("TemporaryBridgeCache");
                RequireSafe(DirectoryPath);
                if (session.Downloads.Count + session.Folders.Count >= MaxEntries)
                    throw new IOException("End file sharing before preparing more files.");
                string directory = "folder-" + Guid.NewGuid().ToString("N");
                string path = Path.Combine(DirectoryPath, directory);
                session.Folders.Add(directory);
                session.Touched = Math.Max(session.Touched, Now()); Save(DirectoryPath, session);
                Directory.CreateDirectory(path); RequireSafe(path); return path;
            }
        }

        public void Touch()
        {
            lock (gate)
            {
                session.Touched = Math.Max(session.Touched, Now());
                Save(DirectoryPath, session);
            }
        }

        public void Retire()
        {
            lock (gate)
            {
                if (retired) return;
                retired = true;
                session.Retired = session.Touched = Math.Max(session.Touched, Now());
                try { Save(DirectoryPath, session); }
                finally { if (uses == 0) Release(); }
            }
        }

        public void Dispose() { Retire(); }

        private void EndUse()
        {
            lock (gate)
            {
                if (--uses < 0) throw new InvalidOperationException("Invalid temporary cache lease.");
                session.Touched = Math.Max(session.Touched, Now());
                if (retired) session.Retired = Math.Max(session.Retired, session.Touched);
                try { Save(DirectoryPath, session); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finally { if (retired && uses == 0) Release(); }
            }
        }

        private void Release() { if (active != null) { active.Dispose(); active = null; } }
        private long Now() { return utcNow().ToUniversalTime().Ticks; }

        public static void Prune(string stateDirectory) { Prune(stateDirectory, DateTime.UtcNow); }

        internal static void Prune(string stateDirectory, DateTime utcNow)
        {
            try
            {
                string root = CacheRoot(stateDirectory);
                if (!Directory.Exists(root)) return;
                RequireSafe(root);
                long now = utcNow.ToUniversalTime().Ticks;
                foreach (string path in Directory.GetDirectories(root))
                {
                    if (!GuidName(Path.GetFileName(path), "")) continue;
                    try { PruneSession(root, path, now); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (System.Security.SecurityException) { }
                    catch (XmlException) { }
                    catch (ArgumentException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            catch (ArgumentException) { }
        }

        private static void PruneSession(string root, string path, long now)
        {
            Inside(root, path); RequireSafe(path);
            Session data = Read(path);
            if (data == null)
            {
                if (!File.Exists(Path.Combine(path, Marker))) AdoptLegacy(path, now);
                return; // First recognition always gets a full grace period.
            }
            string lockPath = Path.Combine(path, Active);
            RequireSafe(lockPath);
            // The owner denies write sharing. A crashed owner's lock is released
            // by Windows; an active session in any dock process is never pruned.
            using (FileStream held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (data.Retired == 0)
                {
                    data.Retired = data.Touched = Math.Max(data.Touched, now);
                    Save(path, data);
                    return;
                }
                if (now < data.Touched || now < data.Retired || now - Math.Max(data.Touched, data.Retired) < GracePeriod.Ticks) return;
                foreach (Download item in data.Downloads)
                {
                    string wrapper = Path.Combine(path, item.Directory);
                    Inside(path, wrapper);
                    if (!Directory.Exists(wrapper)) continue;
                    RequireSafe(wrapper);
                    foreach (string child in Directory.GetDirectories(wrapper))
                    {
                        if (!GuidName(Path.GetFileName(child), "bridge-")) continue;
                        RequireSafe(child);
                        DeleteFile(path, Path.Combine(child, item.Name));
                        DeleteFile(path, Path.Combine(child, ".download.part"));
                        DeleteEmpty(path, child);
                    }
                    DeleteEmpty(path, wrapper);
                }
                foreach (string folder in data.Folders)
                {
                    string wrapper = Path.Combine(path, folder); Inside(path, wrapper);
                    if (!Directory.Exists(wrapper)) continue;
                    try { RequireSafe(wrapper); FolderSnapshot.Prune(wrapper); DeleteEmpty(path, wrapper); }
                    catch (IOException) { } catch (UnauthorizedAccessException) { } catch (XmlException) { }
                }
                foreach (string file in data.Files) DeleteFile(path, Path.Combine(path, file));
                foreach (string directory in data.Directories.OrderByDescending(value => value.Length)) DeleteEmpty(path, Path.Combine(path, directory));
                foreach (string candidate in Directory.GetFiles(path))
                {
                    if (!PendingName(Path.GetFileName(candidate))) continue;
                    try
                    {
                        Session pending = ReadMarker(candidate, data.Id);
                        if (pending != null && pending.Created == data.Created) DeleteFile(path, candidate);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (XmlException) { }
                }
                // Unknown entries, junctions and locked files retain the marker
                // and are retried later; no recursive delete is ever used.
                if (Directory.GetFileSystemEntries(path).Any(entry => Path.GetFileName(entry) != Marker &&
                    Path.GetFileName(entry) != Active)) return;
            }
            DeleteFile(path, lockPath);
            if (File.Exists(lockPath)) return;
            DeleteFile(path, Path.Combine(path, Marker));
            DeleteEmpty(root, path);
        }

        private static Session Read(string path)
        {
            string marker = Path.Combine(path, Marker);
            if (!File.Exists(marker)) return null;
            return ReadMarker(marker, Path.GetFileName(path));
        }

        private static Session ReadMarker(string marker, string sessionId)
        {
            XmlElement element = ReadXml(marker, 1024 * 1024);
            long created, touched, retired;
            if (element == null || element.Name != "session" || element.GetAttribute("owner") != Owner ||
                element.GetAttribute("version") != "1" || element.GetAttribute("id") != sessionId ||
                !Stamp(element.GetAttribute("created"), out created) || !Stamp(element.GetAttribute("touched"), out touched) ||
                !Stamp(element.GetAttribute("retired"), out retired)) return null;
            Session data = new Session { Id = sessionId, Created = created, Touched = touched, Retired = retired };
            foreach (XmlNode node in element.ChildNodes)
            {
                XmlElement child = node as XmlElement;
                if (child == null || data.Downloads.Count + data.Folders.Count + data.Files.Count + data.Directories.Count >= MaxEntries) return null;
                if (child.Name == "download")
                {
                    string directory = child.GetAttribute("directory"), name = child.GetAttribute("name");
                    if (!GuidName(directory, "download-") || !Leaf(name) || name == ".download.part") return null;
                    data.Downloads.Add(new Download { Directory = directory, Name = name });
                }
                else if (child.Name == "folder")
                {
                    string directory = child.GetAttribute("directory");
                    if (!GuidName(directory, "folder-") || data.Folders.Contains(directory)) return null;
                    data.Folders.Add(directory);
                }
                else if (child.Name == "file" || child.Name == "directory")
                {
                    string relative = child.GetAttribute("path");
                    if (!LegacyRelative(relative, child.Name == "directory")) return null;
                    (child.Name == "file" ? data.Files : data.Directories).Add(relative);
                }
                else return null;
            }
            return data;
        }

        private static void Save(string path, Session data)
        {
            RequireSafe(path);
            string pending = Path.Combine(path, PendingPrefix + Guid.NewGuid().ToString("N") + ".part"), marker = Path.Combine(path, Marker);
            RequireSafe(pending); RequireSafe(marker);
            bool created = false;
            try
            {
                // Never open an existing inode for writing: a hard link at a
                // predictable pending name must not truncate another file.
                using (FileStream output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = true }))
                    {
                        writer.WriteStartElement("session"); writer.WriteAttributeString("owner", Owner); writer.WriteAttributeString("version", "1");
                        writer.WriteAttributeString("id", data.Id); writer.WriteAttributeString("created", data.Created.ToString(CultureInfo.InvariantCulture));
                        writer.WriteAttributeString("touched", data.Touched.ToString(CultureInfo.InvariantCulture));
                        writer.WriteAttributeString("retired", data.Retired.ToString(CultureInfo.InvariantCulture));
                        foreach (Download download in data.Downloads)
                        { writer.WriteStartElement("download"); writer.WriteAttributeString("directory", download.Directory); writer.WriteAttributeString("name", download.Name); writer.WriteEndElement(); }
                        foreach (string directory in data.Folders)
                        { writer.WriteStartElement("folder"); writer.WriteAttributeString("directory", directory); writer.WriteEndElement(); }
                        foreach (string file in data.Files)
                        { writer.WriteStartElement("file"); writer.WriteAttributeString("path", file); writer.WriteEndElement(); }
                        foreach (string directory in data.Directories)
                        { writer.WriteStartElement("directory"); writer.WriteAttributeString("path", directory); writer.WriteEndElement(); }
                        writer.WriteEndElement();
                    }
                }
                if (File.Exists(marker)) File.Replace(pending, marker, null);
                else File.Move(pending, marker);
            }
            finally { if (created) DeleteFile(path, pending); }
        }

        // Old caches had no ownership marker. Adopt only an entirely recognized
        // session with valid lease XML identifying every completed source. An
        // ambiguous legacy directory is left alone rather than guessed at.
        private static void AdoptLegacy(string path, long now)
        {
            Session data = new Session { Id = Path.GetFileName(path), Created = now, Touched = now, Retired = now };
            if (Directory.GetFiles(path).Length != 0) return;
            var sourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var leasedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int leases = 0;
            foreach (string child in Directory.GetDirectories(path))
            {
                RequireSafe(child);
                string name = Path.GetFileName(child);
                if (GuidName(name, "bridge-"))
                {
                    if (Directory.GetDirectories(child).Length != 0) return;
                    string[] files = Directory.GetFiles(child);
                    if (files.Length != 1) return;
                    RequireSafe(files[0]);
                    string file = Path.GetFileName(files[0]);
                    if (!Leaf(file)) return;
                    if (file != ".download.part") sourceFiles.Add(Path.GetFullPath(files[0]));
                    data.Files.Add(Path.Combine(name, file)); data.Directories.Add(name);
                }
                else if (name == "leases")
                {
                    if (Directory.GetFiles(child).Length != 0) return;
                    foreach (string identity in Directory.GetDirectories(child))
                    {
                        RequireSafe(identity);
                        string hash = Path.GetFileName(identity);
                        if (!Hex(hash, 64) || Directory.GetDirectories(identity).Length != 0) return;
                        foreach (string file in Directory.GetFiles(identity))
                        {
                            if (++leases >= MaxEntries || !LeaseName(Path.GetFileName(file))) return;
                            XmlElement entry = ReadXml(file, 16384); long stamp;
                            if (entry == null || entry.Name != "ready" || !Stamp(entry.GetAttribute("stamp"), out stamp)) return;
                            string source = Path.GetFullPath(entry.GetAttribute("path"));
                            Inside(path, source); RequireSafe(source);
                            if (Path.GetDirectoryName(Path.GetDirectoryName(source)) != path ||
                                !GuidName(Path.GetFileName(Path.GetDirectoryName(source)), "bridge-") || !File.Exists(source) ||
                                File.GetLastWriteTimeUtc(source).Ticks != stamp) return;
                            leasedFiles.Add(source);
                            data.Files.Add(Path.Combine("leases", hash, Path.GetFileName(file)));
                        }
                        data.Directories.Add(Path.Combine("leases", hash));
                    }
                    data.Directories.Add("leases");
                }
                else return;
                if (data.Files.Count + data.Directories.Count >= MaxEntries) return;
            }
            if (leases == 0 || !sourceFiles.SetEquals(leasedFiles)) return;
            Save(path, data);
        }

        private static XmlElement ReadXml(string path, int limit)
        {
            RequireSafe(path);
            if (new FileInfo(path).Length > limit) return null;
            using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = limit }))
            { XmlDocument document = new XmlDocument { XmlResolver = null }; document.Load(reader); return document.DocumentElement; }
        }

        private static bool LegacyRelative(string value, bool directory)
        {
            if (String.IsNullOrEmpty(value) || Path.IsPathRooted(value)) return false;
            string[] parts = value.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            if (parts.Any(part => !Leaf(part))) return false;
            if (GuidName(parts[0], "bridge-")) return directory ? parts.Length == 1 : parts.Length == 2;
            if (parts[0] != "leases") return false;
            if (directory) return parts.Length == 1 || (parts.Length == 2 && Hex(parts[1], 64));
            return parts.Length == 3 && Hex(parts[1], 64) && LeaseName(parts[2]);
        }

        private static bool LeaseName(string name)
        {
            if (!name.EndsWith(".xml", StringComparison.Ordinal)) return false;
            string[] parts = name.Substring(0, name.Length - 4).Split('-'); long size, modified;
            return parts.Length == 3 && Hex(parts[0], 32) &&
                Int64.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out size) &&
                Int64.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out modified);
        }

        private static bool Stamp(string value, out long ticks)
        { return Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ticks) && ticks <= DateTime.MaxValue.Ticks; }
        private static bool Hex(string value, int length)
        { return value.Length == length && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')); }
        private static bool GuidName(string value, string prefix)
        { return value.StartsWith(prefix, StringComparison.Ordinal) && Hex(value.Substring(prefix.Length), 32); }
        private static bool PendingName(string value)
        {
            if (value == PendingMarker) return true;
            return value.EndsWith(".part", StringComparison.Ordinal) &&
                GuidName(value.Substring(0, value.Length - 5), PendingPrefix);
        }
        private static bool Leaf(string value)
        { return !String.IsNullOrWhiteSpace(value) && value != "." && value != ".." && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && value.TrimEnd(' ', '.') == value; }

        private static string CacheRoot(string stateDirectory)
        {
            if (String.IsNullOrWhiteSpace(stateDirectory)) throw new ArgumentException("A state directory is required.");
            string root = Path.GetFullPath(Path.Combine(stateDirectory, "bridge-cache"));
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Use a local private cache folder.");
            RequireSafe(root);
            return root;
        }

        private static void EnsureDirectory(string path) { RequireSafe(path); Directory.CreateDirectory(path); RequireSafe(path); }
        private static void Inside(string root, string path)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("The temporary source is outside its owned directory.");
        }

        private static void RequireSafe(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { attributes = 0; }
                catch (DirectoryNotFoundException) { attributes = 0; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("A temporary cache path is a reparse point.");
                string parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }

        private static void DeleteFile(string root, string path)
        {
            try { Inside(root, path); RequireSafe(path); if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private static void DeleteEmpty(string root, string path)
        {
            try { Inside(root, path); RequireSafe(path); if (Directory.Exists(path)) Directory.Delete(path, false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed class Session
        {
            public string Id;
            public long Created, Touched, Retired;
            public readonly List<Download> Downloads = new List<Download>();
            public readonly List<string> Folders = new List<string>();
            public readonly List<string> Files = new List<string>();
            public readonly List<string> Directories = new List<string>();
        }
        private sealed class Download { public string Directory, Name; }
        private sealed class Use : IDisposable
        {
            private TemporaryBridgeCache owner;
            public Use(TemporaryBridgeCache owner) { this.owner = owner; }
            public void Dispose() { TemporaryBridgeCache previous = System.Threading.Interlocked.Exchange(ref owner, null); if (previous != null) previous.EndUse(); }
        }
    }
}
