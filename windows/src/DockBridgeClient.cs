using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IPMsg.Dock
{
    public sealed class BridgeItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Mime { get; set; }
        public long Size { get; set; }
        public long Modified { get; set; }
        public bool Thumbnail { get; set; }
        public BridgeItem Snapshot() { return (BridgeItem)MemberwiseClone(); }
    }
    public sealed class BridgeGesture
    {
        public string Id { get; set; }
        public long Revision { get; set; }
        public string Phase { get; set; }
        public List<string> ItemIds { get; set; }
    }
    public sealed class BridgeStatus
    {
        public int Version { get; set; }
        public long ExpiresAt { get; set; }
        public long Revision { get; set; }
        public List<BridgeItem> Items { get; set; }
        public BridgeGesture Gesture { get; set; }
    }

    /// <summary>Authenticated loopback-only client for an owner-approved ADB tunnel.</summary>
    public sealed class DockBridgeClient
    {
        public const long MaximumFileSize = 256L * 1024 * 1024 * 1024;
        private readonly string sessionToken;
        private readonly int downloadIdleTimeoutMs;
        public int Port { get; private set; }
        internal string SessionToken { get { return sessionToken; } }

        public DockBridgeClient(int loopbackPort, string bearerToken)
            : this(loopbackPort, bearerToken, 30000) { }

        // Shorter fixture clocks exercise the production abort path without
        // spending thirty seconds on each generated stalled response.
        internal DockBridgeClient(int loopbackPort, string bearerToken, int idleTimeoutMs)
        {
            if (loopbackPort < 1 || loopbackPort > 65535) throw new ArgumentOutOfRangeException("loopbackPort");
            if (!Hex(bearerToken, 64)) throw new ArgumentException("A valid private bridge session is required.");
            if (idleTimeoutMs < 1 || idleTimeoutMs > 30000) throw new ArgumentOutOfRangeException("idleTimeoutMs");
            Port = loopbackPort; sessionToken = bearerToken; downloadIdleTimeoutMs = idleTimeoutMs;
        }

        public async Task<BridgeStatus> GetStatusAsync(CancellationToken token)
        {
            byte[] bytes = await ReadSmallAsync("/v1/status", "GET", 262144, token).ConfigureAwait(false);
            Dictionary<string, object> root = Obj(StrictJson.Parse(bytes));
            if (Number(root, "version") != 1) throw new IOException("This phone bridge version is not supported.");
            BridgeStatus status = new BridgeStatus { Version = 1, ExpiresAt = Number(root, "expiresAt"),
                Revision = Number(root, "revision"), Items = new List<BridgeItem>() };
            if (status.ExpiresAt <= 0 || status.Revision < 0) throw InvalidResponse();
            List<object> items = Array(root, "items");
            if (items.Count > 30) throw new IOException("The phone shared too many files. Select up to 30 files.");
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (object raw in items)
            {
                Dictionary<string, object> item = Obj(raw);
                BridgeItem selected = new BridgeItem { Id = Text(item, "id"), Name = Text(item, "name"),
                    Mime = Text(item, "mime"), Size = Number(item, "size"), Modified = Number(item, "modified"),
                    Thumbnail = Boolean(item, "thumbnail") };
                ValidateItem(selected);
                if (!ids.Add(selected.Id)) throw InvalidResponse();
                status.Items.Add(selected);
            }
            Dictionary<string, object> gesture = Obj(Value(root, "gesture"));
            status.Gesture = new BridgeGesture { Id = Text(gesture, "id"), Revision = Number(gesture, "revision"),
                Phase = Text(gesture, "phase"), ItemIds = new List<string>() };
            if ((status.Gesture.Id.Length != 0 && !Hex(status.Gesture.Id, 32)) || status.Gesture.Revision < 0 ||
                !(status.Gesture.Phase == "start" || status.Gesture.Phase == "end" || status.Gesture.Phase == "cancel" ||
                  (status.Gesture.Phase.Length == 0 && status.Gesture.Id.Length == 0))) throw InvalidResponse();
            foreach (object id in Array(gesture, "itemIds"))
            {
                string text = id as string;
                if (text == null || !ids.Contains(text) || status.Gesture.ItemIds.Contains(text) || status.Gesture.ItemIds.Count >= 30)
                    throw InvalidResponse();
                status.Gesture.ItemIds.Add(text);
            }
            return status;
        }

        public async Task<byte[]> GetThumbnailAsync(string itemId, CancellationToken token)
        {
            ValidateId(itemId);
            byte[] bytes = await ReadSmallAsync("/v1/thumbnail/" + itemId, "GET", 262144, token).ConfigureAwait(false);
            ValidateJpeg(bytes);
            return bytes;
        }
        public Task StopAsync(CancellationToken token) { return ReadSmallAsync("/v1/stop", "POST", 4096, token); }
        public Task EndGestureAsync(CancellationToken token) { return ReadSmallAsync("/v1/gesture/end", "POST", 4096, token); }
        public Task EndGestureAsync(string gestureId, long revision, CancellationToken token)
        {
            if (!Hex(gestureId, 32) || revision < 0) throw new ArgumentException("A current phone gesture is required.");
            return ReadSmallAsync("/v1/gesture/end", "POST", 4096, token, gestureId, revision);
        }

        public async Task<string> DownloadAsync(BridgeItem item, string privateCacheDirectory,
            IProgress<long> progress, CancellationToken token)
        {
            ValidateItem(item);
            BridgeItem selected = item.Snapshot();
            string root;
            try { root = Path.GetFullPath(privateCacheDirectory); }
            catch (PathTooLongException) { throw VirtualFileDrag.CachePathTooLong(); }
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Use a local private cache folder.");
            string owned = Path.Combine(root, "bridge-" + Guid.NewGuid().ToString("N"));
            string completed = Path.Combine(owned, VirtualFileDrag.ExportNameForDirectory(selected.Name, owned));
            Directory.CreateDirectory(root);
            DriveInfo disk = new DriveInfo(Path.GetPathRoot(root));
            if (selected.Size > Math.Max(0, disk.AvailableFreeSpace - 4L * 1024 * 1024)) throw new IOException("Windows needs more space for this file.");
            Directory.CreateDirectory(owned);
            string partial = Path.Combine(owned, ".download.part");
            bool success = false;
            try
            {
                using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (CancellationTokenSource idle = new CancellationTokenSource())
                using (CancellationTokenSource transfer = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, idle.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromMinutes(15));
                    HttpWebRequest request = Request("/v1/file/" + selected.Id, "GET");
                    using (transfer.Token.Register(delegate { request.Abort(); }))
                    {
                        try
                        {
                            // HttpWebRequest's synchronous timeouts do not bound
                            // these async awaits. Abort the socket on a separate
                            // idle clock, while retaining the total deadline.
                            idle.CancelAfter(downloadIdleTimeoutMs);
                            using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                            {
                                idle.CancelAfter(Timeout.Infinite);
                                ThrowDownloadCancellation(token, deadline, idle);
                                CheckResponse(response);
                                if (response.ContentLength != selected.Size) throw new IOException("The shared file changed. Select it again on the phone.");
                                using (Stream input = response.GetResponseStream())
                                using (FileStream output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                                {
                                    byte[] buffer = new byte[81920];
                                    long received = 0;
                                    Stopwatch updates = Stopwatch.StartNew(); long lastUpdate = 0;
                                    while (true)
                                    {
                                        idle.CancelAfter(downloadIdleTimeoutMs);
                                        int count = await input.ReadAsync(buffer, 0, buffer.Length, transfer.Token).ConfigureAwait(false);
                                        idle.CancelAfter(Timeout.Infinite);
                                        ThrowDownloadCancellation(token, deadline, idle);
                                        if (count == 0) break;
                                        if (count > selected.Size - received) throw new IOException("The phone sent more data than advertised.");
                                        await output.WriteAsync(buffer, 0, count, deadline.Token).ConfigureAwait(false);
                                        received += count;
                                        if (progress != null && updates.ElapsedMilliseconds - lastUpdate >= 125)
                                        { progress.Report(received); lastUpdate = updates.ElapsedMilliseconds; }
                                    }
                                    if (received != selected.Size) throw new IOException("The file ended before its advertised size. Retry the transfer.");
                                    await output.FlushAsync(deadline.Token).ConfigureAwait(false);
                                    if (output.Length != selected.Size) throw new IOException("The saved copy could not be verified.");
                                }
                            }
                        }
                        catch (WebException) { ThrowDownloadCancellation(token, deadline, idle); throw new IOException("The phone bridge disconnected or refused this file. Reopen file sharing on the phone."); }
                        catch (IOException) { ThrowDownloadCancellation(token, deadline, idle); throw; }
                        catch (ObjectDisposedException) { ThrowDownloadCancellation(token, deadline, idle); throw new IOException("The phone bridge disconnected during the transfer. Reopen file sharing on the phone."); }
                        catch (OperationCanceledException) { ThrowDownloadCancellation(token, deadline, idle); throw; }
                    }
                    ThrowDownloadCancellation(token, deadline, idle);
                }
                token.ThrowIfCancellationRequested();
                File.Move(partial, completed);
                success = true;
                if (progress != null) progress.Report(selected.Size);
                return completed;
            }
            finally
            {
                if (!success)
                {
                    try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    try { Directory.Delete(owned, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }

        private async Task<byte[]> ReadSmallAsync(string path, string method, int limit, CancellationToken token, string gestureId = null, long gestureRevision = -1)
        {
            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(8000);
                HttpWebRequest request = Request(path, method);
                if (gestureId != null)
                {
                    request.Headers["X-IPMsg-Gesture-Id"] = gestureId;
                    request.Headers["X-IPMsg-Gesture-Revision"] = gestureRevision.ToString(CultureInfo.InvariantCulture);
                }
                using (deadline.Token.Register(delegate { request.Abort(); }))
                {
                    try
                    {
                        using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                        {
                            CheckResponse(response);
                            if (response.ContentLength > limit) throw InvalidResponse();
                            using (Stream stream = response.GetResponseStream())
                            using (MemoryStream output = new MemoryStream())
                            {
                                byte[] buffer = new byte[8192]; int count;
                                while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) != 0)
                                {
                                    if (output.Length + count > limit) throw InvalidResponse();
                                    output.Write(buffer, 0, count);
                                }
                                if (response.ContentLength >= 0 && output.Length != response.ContentLength) throw InvalidResponse();
                                return output.ToArray();
                            }
                        }
                    }
                    catch (WebException) { if (deadline.IsCancellationRequested) ThrowCancelled(token); throw new IOException("Phone file sharing is unavailable. Confirm the session on your phone."); }
                    catch (IOException) { if (deadline.IsCancellationRequested) ThrowCancelled(token); throw; }
                    catch (ObjectDisposedException) { if (deadline.IsCancellationRequested) ThrowCancelled(token); throw new IOException("Phone file sharing disconnected. Confirm the session on your phone."); }
                }
            }
        }
        private HttpWebRequest Request(string path, string method)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + path);
            request.Proxy = null; request.AllowAutoRedirect = false; request.UseDefaultCredentials = false;
            request.Credentials = null; request.PreAuthenticate = false; request.KeepAlive = false;
            request.Method = method; request.Timeout = 8000; request.ReadWriteTimeout = 10000;
            request.ServicePoint.ConnectionLimit = 8;
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + sessionToken;
            if (method == "POST") request.ContentLength = 0;
            return request;
        }
        private static void CheckResponse(HttpWebResponse response)
        {
            if (response.StatusCode != HttpStatusCode.OK || response.ResponseUri.Host != "127.0.0.1" || response.ResponseUri.Scheme != "http")
                throw new IOException("The phone bridge returned an unexpected response.");
        }
        private static void ThrowCancelled(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("The phone transfer timed out. Reopen sharing and retry.");
        }
        private static void ThrowDownloadCancellation(CancellationToken token, CancellationTokenSource deadline,
            CancellationTokenSource idle)
        {
            token.ThrowIfCancellationRequested();
            if (idle.IsCancellationRequested)
                throw new TimeoutException("The phone file stopped responding. Check the connection, then choose Prepare / retry selected.");
            if (deadline.IsCancellationRequested)
                throw new TimeoutException("The phone transfer timed out. Reopen sharing and retry.");
        }
        internal static bool Hex(string value, int length)
        {
            return value != null && value.Length == length && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }
        internal static void ValidateId(string id) { if (!Hex(id, 32)) throw new IOException("Invalid shared-file identity."); }
        internal static void ValidateItem(BridgeItem item)
        {
            if (item == null) throw InvalidResponse();
            ValidateId(item.Id);
            if (String.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 512 || item.Name.Any(Char.IsControl) ||
                item.Name.IndexOf('/') >= 0 || item.Name.IndexOf('\\') >= 0 || item.Name == "." || item.Name == ".." ||
                item.Size < 0 || item.Size > MaximumFileSize || item.Modified < 0 || item.Modified > 253402300799999L || item.Mime == null || item.Mime.Length > 128 || item.Mime.Any(Char.IsControl))
                throw InvalidResponse();
            if (item.Mime == "application/vnd.ipmsg.folder+zip" &&
                (item.Size > 32L * 1024 * 1024 * 1024 || !item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                throw InvalidResponse();
        }
        private static IOException InvalidResponse() { return new IOException("The phone bridge returned invalid or incomplete data."); }
        private static Dictionary<string, object> Obj(object value) { var result = value as Dictionary<string, object>; if (result == null) throw InvalidResponse(); return result; }
        private static object Value(Dictionary<string, object> obj, string key) { object value; if (!obj.TryGetValue(key, out value)) throw InvalidResponse(); return value; }
        private static string Text(Dictionary<string, object> obj, string key) { var value = Value(obj, key) as string; if (value == null) throw InvalidResponse(); return value; }
        private static long Number(Dictionary<string, object> obj, string key) { var value = Value(obj, key); if (!(value is long)) throw InvalidResponse(); return (long)value; }
        private static bool Boolean(Dictionary<string, object> obj, string key) { var value = Value(obj, key); if (!(value is bool)) throw InvalidResponse(); return (bool)value; }
        private static List<object> Array(Dictionary<string, object> obj, string key) { var value = Value(obj, key) as List<object>; if (value == null) throw InvalidResponse(); return value; }

        private static void ValidateJpeg(byte[] bytes)
        {
            if (bytes.Length < 4 || bytes[0] != 255 || bytes[1] != 216) throw InvalidResponse();
            int offset = 2;
            while (offset + 4 <= bytes.Length)
            {
                if (bytes[offset++] != 255) throw InvalidResponse();
                while (offset < bytes.Length && bytes[offset] == 255) offset++;
                if (offset >= bytes.Length) break;
                int marker = bytes[offset++];
                if (marker == 217 || marker == 218) break;
                if (offset + 2 > bytes.Length) throw InvalidResponse();
                int length = (bytes[offset] << 8) | bytes[offset + 1];
                if (length < 2 || offset + length > bytes.Length) throw InvalidResponse();
                if (marker >= 192 && marker <= 195)
                {
                    if (length < 8) throw InvalidResponse();
                    int height = (bytes[offset + 3] << 8) | bytes[offset + 4];
                    int width = (bytes[offset + 5] << 8) | bytes[offset + 6];
                    if (width < 1 || height < 1 || width > 1024 || height > 1024) throw InvalidResponse();
                    return;
                }
                offset += length;
            }
            throw InvalidResponse();
        }

        private sealed class StrictJson
        {
            private readonly string text; private int offset;
            private StrictJson(string text) { this.text = text; }
            public static object Parse(byte[] bytes)
            {
                string text;
                try { text = new UTF8Encoding(false, true).GetString(bytes); } catch (DecoderFallbackException) { throw InvalidResponse(); }
                StrictJson reader = new StrictJson(text); object value = reader.Read(0); reader.Space();
                if (reader.offset != text.Length) throw InvalidResponse(); return value;
            }
            private void Space() { while (offset < text.Length && (text[offset] == ' ' || text[offset] == '\r' || text[offset] == '\n' || text[offset] == '\t')) offset++; }
            private object Read(int depth)
            {
                if (depth > 8) throw InvalidResponse(); Space(); if (offset >= text.Length) throw InvalidResponse();
                char current = text[offset];
                if (current == '"') return String();
                if (current == '{')
                {
                    offset++; var obj = new Dictionary<string, object>(StringComparer.Ordinal); Space(); if (Take('}')) return obj;
                    do { Space(); string key = String(); if (key.Length > 64 || obj.ContainsKey(key) || obj.Count >= 64) throw InvalidResponse(); Space(); if (!Take(':')) throw InvalidResponse(); obj.Add(key, Read(depth + 1)); Space(); if (Take('}')) return obj; } while (Take(','));
                    throw InvalidResponse();
                }
                if (current == '[')
                {
                    offset++; var array = new List<object>(); Space(); if (Take(']')) return array;
                    do { if (array.Count >= 64) throw InvalidResponse(); array.Add(Read(depth + 1)); Space(); if (Take(']')) return array; } while (Take(',')); throw InvalidResponse();
                }
                if (Word("true")) return true; if (Word("false")) return false; if (Word("null")) return null;
                int start = offset; if (Take('-')) { }
                if (offset >= text.Length || text[offset] < '0' || text[offset] > '9') throw InvalidResponse();
                if (text[offset] == '0') offset++; else while (offset < text.Length && text[offset] >= '0' && text[offset] <= '9') offset++;
                long number; if (!Int64.TryParse(text.Substring(start, offset - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number)) throw InvalidResponse();
                return number;
            }
            private bool Word(string value) { if (offset + value.Length > text.Length || System.String.CompareOrdinal(text, offset, value, 0, value.Length) != 0) return false; offset += value.Length; return true; }
            private bool Take(char value) { if (offset >= text.Length || text[offset] != value) return false; offset++; return true; }
            private string String()
            {
                if (!Take('"')) throw InvalidResponse(); StringBuilder result = new StringBuilder();
                while (offset < text.Length)
                {
                    char c = text[offset++]; if (c == '"')
                    {
                        string value = result.ToString();
                        for (int i = 0; i < value.Length; i++)
                            if (Char.IsSurrogate(value[i])) { if (!Char.IsHighSurrogate(value[i]) || i + 1 >= value.Length || !Char.IsLowSurrogate(value[++i])) throw InvalidResponse(); }
                        return value;
                    }
                    if (c < 32) throw InvalidResponse();
                    if (c == '\\')
                    {
                        if (offset >= text.Length) throw InvalidResponse(); c = text[offset++];
                        switch (c)
                        {
                            case '"': case '\\': case '/': break;
                            case 'b': c = '\b'; break; case 'f': c = '\f'; break; case 'n': c = '\n'; break; case 'r': c = '\r'; break; case 't': c = '\t'; break;
                            case 'u': int parsed; if (offset + 4 > text.Length || !Int32.TryParse(text.Substring(offset, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed)) throw InvalidResponse(); c = (char)parsed; offset += 4; break;
                            default: throw InvalidResponse();
                        }
                    }
                    result.Append(c); if (result.Length > 4096) throw InvalidResponse();
                }
                throw InvalidResponse();
            }
        }
    }
}
