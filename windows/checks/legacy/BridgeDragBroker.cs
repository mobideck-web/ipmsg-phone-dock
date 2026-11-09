using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace IPMsg.Dock
{
    public static class BridgeDragBroker
    {
        public static int Run()
        {
            StreamReader protocolInput = null;
            StreamWriter protocolOutput = null;
            bool oleInitialized = false;
            BridgeDragResult result = BridgeFileDrag.Result(BridgeDragOutcome.Failed, "The isolated file drag could not finish.");
            try
            {
                // This is a GUI executable with redirected pipes, not an attached console.
                // Console encoding setters call console APIs that can fail before reading
                // the request, closing stdin under the parent's pending write.
                protocolInput = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true), false, 4096, true);
                protocolOutput = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 4096, true);
                if (BridgeOle.OleInitialize(IntPtr.Zero) < 0) throw new IOException("Could not initialize Windows file dragging.");
                oleInitialized = true;
                string line = ReadLine(protocolInput, 100000);
                if (line == null) throw new IOException("Missing drag request.");
                byte[] bytes = Convert.FromBase64String(line);
                XmlElement request = BridgeFileDrag.ParseXml(new UTF8Encoding(false, true).GetString(bytes), 65536).DocumentElement;
                if (request == null || request.Name != "drag") throw new IOException("Invalid drag request.");
                int port; if (!Int32.TryParse(request.GetAttribute("port"), out port)) throw new IOException("Invalid bridge port.");
                DockBridgeClient client = new DockBridgeClient(port, request.GetAttribute("token"));
                string cache = Path.GetFullPath(request.GetAttribute("cache"));
                if (cache.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Use a local private cache folder.");
                List<BridgeItem> items = new List<BridgeItem>();
                foreach (XmlNode node in request.ChildNodes)
                {
                    XmlElement element = node as XmlElement;
                    long size, modified;
                    if (element == null || element.Name != "file" || items.Count >= 30 ||
                        !Int64.TryParse(element.GetAttribute("size"), NumberStyles.None, CultureInfo.InvariantCulture, out size) ||
                        !Int64.TryParse(element.GetAttribute("modified"), NumberStyles.None, CultureInfo.InvariantCulture, out modified)) throw new IOException("Invalid shared file.");
                    BridgeItem item = new BridgeItem { Id = element.GetAttribute("id"), Name = element.GetAttribute("name"),
                        Mime = element.GetAttribute("mime"), Size = size, Modified = modified };
                    DockBridgeClient.ValidateItem(item);
                    if (items.Any(i => i.Id == item.Id)) throw new IOException("Duplicate shared file.");
                    items.Add(item);
                }
                if (items.Count == 0) throw new IOException("No shared files.");
                if ((BridgeOle.GetAsyncKeyState(1) & 0x8000) == 0)
                    result = BridgeFileDrag.Result(BridgeDragOutcome.Cancelled, "Drag again while holding the mouse button.");
                else using (CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(18)))
                {
                    // stdin stays private and open: EOF means the parent disappeared.
                    Task input = Task.Run(delegate { try { string command = ReadLine(protocolInput, 16); if (command == null || command == "cancel") cancellation.Cancel(); } catch (Exception) { cancellation.Cancel(); } });
                    BridgeLeaseStore leases = new BridgeLeaseStore(client, items, cache, cancellation.Token);
                    BridgeDataObject data = new BridgeDataObject(items, leases.Tasks, cancellation.Token);
                    int effect;
                    int code = BridgeOle.DoDragDrop(data, new BridgeDropSource(cancellation.Token), 1, out effect);
                    bool dropped = code == 0x40100 && (effect & 1) != 0;
                    result = CompleteDrop(data, leases.All, dropped, cancellation);
                    foreach (KeyValuePair<string, Task<string>> copy in leases.Tasks)
                        if (copy.Value.Status == TaskStatus.RanToCompletion) result.ReadyFiles.Add(copy.Key, copy.Value.Result);
                }
            }
            catch (OperationCanceledException) { result = BridgeFileDrag.Result(BridgeDragOutcome.Cancelled, "File drag cancelled or timed out."); }
            catch (Exception) { result = BridgeFileDrag.Result(BridgeDragOutcome.Failed, "The phone disconnected or the receiving application refused this file. Retry from the file shelf."); }
            finally { if (oleInitialized) BridgeOle.OleUninitialize(); }
            StringBuilder response = new StringBuilder();
            using (XmlWriter writer = XmlWriter.Create(response, new XmlWriterSettings { OmitXmlDeclaration = true }))
            {
                writer.WriteStartElement("result"); writer.WriteAttributeString("outcome", result.Outcome.ToString()); writer.WriteAttributeString("message", result.Message);
                foreach (KeyValuePair<string, string> item in result.ReadyFiles)
                { writer.WriteStartElement("ready"); writer.WriteAttributeString("id", item.Key); writer.WriteAttributeString("path", item.Value); writer.WriteEndElement(); }
                writer.WriteEndElement();
            }
            if (protocolOutput == null) return 1;
            try { protocolOutput.Write(response.ToString()); protocolOutput.Flush(); }
            catch (IOException) { return 1; } // Parent closed; no console error dialog or token logging.
            // The cancellation reader can still be waiting on stdin. Process exit owns
            // these inherited handles; do not synchronously dispose an active pipe read.
            return result.Outcome == BridgeDragOutcome.Failed ? 1 : 0;
        }
        internal static BridgeDragResult CompleteDrop(BridgeDataObject data, Task copies, bool dropped, CancellationTokenSource cancellation)
        {
            var sourceFailure = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (Task preparation in data.CopyTasks)
            {
                Task watched = preparation.ContinueWith(delegate(Task completed)
                {
                    if (completed.IsFaulted || completed.IsCanceled) sourceFailure.TrySetResult(completed);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            // A failed source must not wait for an EndOperation that a target may
            // omit after its stream read failed. Observe the aggregate too, even
            // when a first failure lets this broker leave before other workers.
            Task observed = copies.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                // A target may probe a pending stream synchronously, then negotiate
                // an async drop. That earlier retry flag must never shorten the COM
                // server's lifetime after a real drop: EndOperation owns completion.
                if (dropped)
                {
                    if (data.AsyncStarted)
                    {
                        WaitWithCom(Task.WhenAny(data.Ended.Task, sourceFailure.Task), cancellation.Token);
                        ThrowSourceFailure(sourceFailure.Task);
                        if (data.OperationFailed) throw new IOException("The receiving application did not finish copying the files.");
                    }
                    WaitWithCom(Task.WhenAny(copies, sourceFailure.Task), cancellation.Token);
                    ThrowSourceFailure(sourceFailure.Task);
                    copies.GetAwaiter().GetResult();
                    if (data.OperationFailed) throw new IOException("The receiving application did not finish copying the files.");
                    return BridgeFileDrag.Result(BridgeDragOutcome.Completed, "Files handed to the receiving application.");
                }
                if (data.NeedsReadyRetry)
                {
                    WaitWithCom(Task.WhenAny(copies, sourceFailure.Task), cancellation.Token);
                    ThrowSourceFailure(sourceFailure.Task);
                    copies.GetAwaiter().GetResult();
                    return BridgeFileDrag.Result(BridgeDragOutcome.ReadyForRetry, "Files are ready. Drag again to this application.");
                }
                cancellation.Cancel();
                return BridgeFileDrag.Result(BridgeDragOutcome.Cancelled, "File drag cancelled.");
            }
            catch { cancellation.Cancel(); throw; }
        }
        private static void ThrowSourceFailure(Task<Task> failure)
        {
            if (failure.IsCompleted) failure.GetAwaiter().GetResult().GetAwaiter().GetResult();
        }
        private static string ReadLine(TextReader input, int limit)
        {
            StringBuilder text = new StringBuilder(); int character;
            while ((character = input.Read()) >= 0)
            {
                if (character == '\n') return text.ToString().TrimEnd('\r');
                if (text.Length >= limit) throw new IOException("Drag message is too large.");
                text.Append((char)character);
            }
            return text.Length == 0 ? null : text.ToString();
        }
        private static void WaitWithCom(Task task, CancellationToken token)
        {
            using (WaitHandle finished = ((IAsyncResult)task).AsyncWaitHandle)
            {
                IntPtr[] handles = { finished.SafeWaitHandle.DangerousGetHandle(), token.WaitHandle.SafeWaitHandle.DangerousGetHandle() };
                while (!task.IsCompleted)
                {
                    token.ThrowIfCancellationRequested(); uint index;
                    BridgeOle.CoWaitForMultipleHandles(0x18, 250, 2, handles, out index);
                }
            }
            token.ThrowIfCancellationRequested();
            task.GetAwaiter().GetResult(); // Already completed; only the isolated broker pumps COM here.
        }
    }

    internal sealed class BridgeLeaseStore
    {
        public readonly Dictionary<string, Task<string>> Tasks = new Dictionary<string, Task<string>>(StringComparer.Ordinal);
        public readonly Task All;
        private readonly SemaphoreSlim queue = new SemaphoreSlim(2, 2);
        private readonly string cacheRoot;
        private readonly string leaseDirectory;
        public BridgeLeaseStore(DockBridgeClient client, IList<BridgeItem> items, string cache, CancellationToken token)
        {
            cacheRoot = Path.GetFullPath(cache).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string identity;
            using (SHA256 sha = SHA256.Create()) identity = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(client.SessionToken))).Replace("-", "").ToLowerInvariant();
            leaseDirectory = Path.Combine(cacheRoot, "leases", identity);
            Directory.CreateDirectory(leaseDirectory);
            foreach (BridgeItem selected in items)
            {
                BridgeItem item = selected.Snapshot();
                string ready = ReadLease(item);
                Tasks.Add(item.Id, ready != null ? Task.FromResult(ready) : Task.Run(async delegate
                {
                    await queue.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        string completed = await client.DownloadAsync(item, cacheRoot, null, token).ConfigureAwait(false);
                        SaveLease(item, completed); return completed;
                    }
                    finally { queue.Release(); }
                }, token));
            }
            All = Task.WhenAll(Tasks.Values);
        }
        private string Manifest(BridgeItem item) { return Path.Combine(leaseDirectory, item.Id + "-" + item.Size.ToString(CultureInfo.InvariantCulture) + "-" + item.Modified.ToString(CultureInfo.InvariantCulture) + ".xml"); }
        private string ReadLease(BridgeItem item)
        {
            try
            {
                string manifest = Manifest(item);
                if (!File.Exists(manifest) || new FileInfo(manifest).Length > 16384) return null;
                XmlElement entry = BridgeFileDrag.ParseXml(File.ReadAllText(manifest), 16384).DocumentElement;
                long stamp;
                if (entry == null || entry.Name != "ready" || !Int64.TryParse(entry.GetAttribute("stamp"), out stamp)) return null;
                string path = Path.GetFullPath(entry.GetAttribute("path"));
                if (!path.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase)) return null;
                FileInfo file = new FileInfo(path);
                return file.Exists && file.Length == item.Size && file.LastWriteTimeUtc.Ticks == stamp ? path : null;
            }
            catch (Exception) { return null; }
        }
        private void SaveLease(BridgeItem item, string path)
        {
            string temporary = Path.Combine(leaseDirectory, Guid.NewGuid().ToString("N") + ".part");
            try
            {
                using (XmlWriter writer = XmlWriter.Create(temporary, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = true }))
                { writer.WriteStartElement("ready"); writer.WriteAttributeString("path", path); writer.WriteAttributeString("stamp", File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture)); writer.WriteEndElement(); }
                string target = Manifest(item);
                if (File.Exists(target)) File.Delete(target);
                File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    [ComVisible(true), Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IBridgeAsyncCapability
    {
        [PreserveSig] int SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool enabled);
        [PreserveSig] int GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool enabled);
        [PreserveSig] int StartOperation(IBindCtx reserved);
        [PreserveSig] int InOperation([MarshalAs(UnmanagedType.Bool)] out bool active);
        [PreserveSig] int EndOperation(int result, IBindCtx reserved, uint effects);
    }
    [ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IBridgeDropSource
    {
        [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keyState);
        [PreserveSig] int GiveFeedback(uint effect);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class BridgeDropSource : IBridgeDropSource
    {
        private readonly CancellationToken token;
        public BridgeDropSource(CancellationToken token) { this.token = token; }
        public int QueryContinueDrag(bool escape, uint keys) { return escape || token.IsCancellationRequested ? 0x40101 : (keys & 1) == 0 ? 0x40100 : 0; }
        public int GiveFeedback(uint effect) { return 0x40102; }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class BridgeDataObject : System.Runtime.InteropServices.ComTypes.IDataObject, IBridgeAsyncCapability
    {
        private readonly IList<BridgeItem> items;
        private readonly IDictionary<string, Task<string>> copies;
        private readonly System.Runtime.InteropServices.ComTypes.IDataObject localData;
        private readonly CancellationToken token;
        private readonly int sourceThread = Thread.CurrentThread.ManagedThreadId;
        private readonly object gate = new object();
        private bool mode = true, active, started, failed, retry;
        internal readonly TaskCompletionSource<bool> Ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool NeedsReadyRetry { get { lock (gate) return retry; } }
        internal bool AsyncStarted { get { lock (gate) return started; } }
        internal bool OperationFailed { get { lock (gate) return failed; } }
        internal IEnumerable<Task<string>> CopyTasks { get { return copies.Values; } }
        internal const short FileDrop = 15;
        internal static readonly short Descriptor = unchecked((short)BridgeOle.RegisterClipboardFormat("FileGroupDescriptorW"));
        internal static readonly short Contents = unchecked((short)BridgeOle.RegisterClipboardFormat("FileContents"));
        internal static readonly short Preferred = unchecked((short)BridgeOle.RegisterClipboardFormat("Preferred DropEffect"));
        public BridgeDataObject(IList<BridgeItem> items, IDictionary<string, Task<string>> copies, CancellationToken token)
        {
            this.items = items; this.copies = copies; this.token = token;
            // Freeze the offered formats at drag startup. Only completed local
            // originals may be exposed through Windows' standard path format.
            if (copies.Values.All(task => task.Status == TaskStatus.RanToCompletion))
                localData = VirtualFileDrag.CreateLocalDataObject(items.Select(item => copies[item.Id].Result).ToList());
        }
        internal bool CanWait { get { lock (gate) return active && mode; } }
        internal void RequireRetry() { lock (gate) retry = true; }
        public int SetAsyncMode(bool enabled) { lock (gate) mode = enabled; return 0; }
        public int GetAsyncMode(out bool enabled) { lock (gate) enabled = mode; return 0; }
        public int StartOperation(IBindCtx reserved) { lock (gate) { if (!mode) return unchecked((int)0x80004005); active = started = true; } return 0; }
        public int InOperation(out bool running) { lock (gate) running = active; return 0; }
        public int EndOperation(int result, IBindCtx reserved, uint effects) { lock (gate) { active = false; failed = result < 0 || (effects & 1) == 0; } Ended.TrySetResult(true); return 0; }
        public int QueryGetData(ref FORMATETC format)
        {
            if (format.cfFormat == FileDrop && localData != null) return localData.QueryGetData(ref format);
            if (format.dwAspect != DVASPECT.DVASPECT_CONTENT || format.ptd != IntPtr.Zero) return unchecked((int)0x80040064);
            if ((format.cfFormat == Descriptor || format.cfFormat == Preferred) && (format.tymed & TYMED.TYMED_HGLOBAL) != 0 && format.lindex == -1) return 0;
            if (format.cfFormat == Contents && (format.tymed & TYMED.TYMED_ISTREAM) != 0 && format.lindex >= 0 && format.lindex < items.Count) return 0;
            return unchecked((int)0x80040064);
        }
        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = new STGMEDIUM(); int valid = QueryGetData(ref format); if (valid != 0) Marshal.ThrowExceptionForHR(valid);
            if (format.cfFormat == FileDrop)
                localData.GetData(ref format, out medium);
            else if (format.cfFormat == Descriptor)
            {
                int size = Marshal.SizeOf(typeof(BridgeDescriptor));
                IntPtr memory = BridgeOle.GlobalAlloc(0x42, (UIntPtr)(4 + size * items.Count));
                if (memory == IntPtr.Zero) throw new OutOfMemoryException();
                IntPtr pointer = BridgeOle.GlobalLock(memory);
                if (pointer == IntPtr.Zero) { BridgeOle.GlobalFree(memory); throw new OutOfMemoryException(); }
                try
                {
                    Marshal.WriteInt32(pointer, items.Count);
                    HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < items.Count; i++)
                    {
                        BridgeItem item = items[i]; string baseName = VirtualFileDrag.ExportName(item.Name); string name = baseName;
                        for (int suffix = 2; !names.Add(name); suffix++) name = Path.GetFileNameWithoutExtension(baseName) + " (" + suffix + ")" + Path.GetExtension(baseName);
                        long stamp = item.Modified > 0 && item.Modified <= 253402300799999L ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(item.Modified).ToFileTimeUtc() : 0;
                        BridgeDescriptor descriptor = new BridgeDescriptor { Flags = 0x4 | 0x40 | 0x4000 | (stamp > 0 ? 0x20u : 0u), Attributes = 0x80,
                            SizeHigh = (uint)((ulong)item.Size >> 32), SizeLow = (uint)item.Size, Name = name,
                            WriteTime = new System.Runtime.InteropServices.ComTypes.FILETIME { dwHighDateTime = (int)(stamp >> 32), dwLowDateTime = (int)stamp } };
                        Marshal.StructureToPtr(descriptor, IntPtr.Add(pointer, 4 + size * i), false);
                    }
                }
                catch { BridgeOle.GlobalUnlock(memory); BridgeOle.GlobalFree(memory); throw; }
                BridgeOle.GlobalUnlock(memory); medium.tymed = TYMED.TYMED_HGLOBAL; medium.unionmember = memory;
            }
            else if (format.cfFormat == Preferred)
            {
                IntPtr memory = BridgeOle.GlobalAlloc(0x42, (UIntPtr)4); IntPtr pointer = BridgeOle.GlobalLock(memory);
                if (pointer == IntPtr.Zero) { if (memory != IntPtr.Zero) BridgeOle.GlobalFree(memory); throw new OutOfMemoryException(); }
                Marshal.WriteInt32(pointer, 1); BridgeOle.GlobalUnlock(memory); medium.tymed = TYMED.TYMED_HGLOBAL; medium.unionmember = memory;
            }
            else
            {
                BridgeItem item = items[format.lindex]; Task<string> copy = copies[item.Id];
                if (!CanWait && copies.Values.Any(task => task.Status != TaskStatus.RanToCompletion))
                { RequireRetry(); throw new COMException("This application needs ready local copies. Drag again when ready.", unchecked((int)0x8000000A)); }
                BridgeReadStream stream = new BridgeReadStream(item, copy, this, sourceThread, token, 0);
                medium.tymed = TYMED.TYMED_ISTREAM; medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
            }
        }
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) { throw new COMException("Unsupported medium.", unchecked((int)0x80040069)); }
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = IntPtr.Zero; return 0x40130; }
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) { if (release) BridgeOle.ReleaseStgMedium(ref medium); }
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
        {
            if (direction != DATADIR.DATADIR_GET) throw new COMException("Unsupported direction.", unchecked((int)0x80004001));
            List<FORMATETC> formats = new List<FORMATETC>();
            if (localData != null) formats.Add(Format(FileDrop, TYMED.TYMED_HGLOBAL, -1));
            formats.AddRange(new[] { Format(Descriptor, TYMED.TYMED_HGLOBAL, -1), Format(Contents, TYMED.TYMED_ISTREAM, 0), Format(Preferred, TYMED.TYMED_HGLOBAL, -1) });
            return new BridgeFormats(formats.ToArray());
        }
        internal static FORMATETC Format(short format, TYMED medium, int index) { return new FORMATETC { cfFormat = format, tymed = medium, lindex = index, dwAspect = DVASPECT.DVASPECT_CONTENT, ptd = IntPtr.Zero }; }
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) { throw new COMException("Advises are not supported.", unchecked((int)0x80040003)); }
        public int EnumDAdvise(out IEnumSTATDATA result) { result = null; return unchecked((int)0x80040003); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class BridgeReadStream : IStream
    {
        private readonly BridgeItem item; private readonly Task<string> copy; private readonly BridgeDataObject owner;
        private readonly int sourceThread; private readonly CancellationToken token; private readonly object gate = new object();
        private FileStream file; private long position;
        public BridgeReadStream(BridgeItem item, Task<string> copy, BridgeDataObject owner, int sourceThread, CancellationToken token, long position)
        { this.item = item; this.copy = copy; this.owner = owner; this.sourceThread = sourceThread; this.token = token; this.position = position; }
        ~BridgeReadStream() { if (file != null) file.Dispose(); }
        private string ReadyPath()
        {
            token.ThrowIfCancellationRequested();
            if (!copy.IsCompleted)
            {
                if (Thread.CurrentThread.ManagedThreadId == sourceThread || !owner.CanWait)
                { owner.RequireRetry(); throw new COMException("A completed local copy is required.", unchecked((int)0x8000000A)); }
                // Only an async target's worker may wait. Never wait on the source COM/UI apartment.
                int ready = WaitHandle.WaitAny(new[] { ((IAsyncResult)copy).AsyncWaitHandle, token.WaitHandle }, TimeSpan.FromMinutes(15));
                token.ThrowIfCancellationRequested(); if (ready == WaitHandle.WaitTimeout) throw new COMException("The phone transfer timed out.", unchecked((int)0x8003001E));
            }
            try { return copy.GetAwaiter().GetResult(); } catch (Exception) { throw new COMException("The phone transfer failed.", unchecked((int)0x8003001E)); }
        }
        private FileStream Open(string path)
        {
            if (file == null)
            {
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length != item.Size) { file.Dispose(); file = null; throw new COMException("The cached copy changed.", unchecked((int)0x8003001E)); }
            }
            file.Position = position; return file;
        }
        public void Read(byte[] buffer, int count, IntPtr read)
        {
            if (buffer == null || count < 0 || count > buffer.Length) throw new COMException("Invalid read buffer.", unchecked((int)0x80030057));
            // Never retain the stream-position lock while waiting for a network worker:
            // a source-apartment seek/clone/read must remain able to fail immediately.
            string path = count == 0 ? null : ReadyPath();
            int received; lock (gate) { received = count == 0 ? 0 : Open(path).Read(buffer, 0, count); position += received; }
            if (read != IntPtr.Zero) Marshal.WriteInt32(read, received);
        }
        public void Seek(long movement, int origin, IntPtr result)
        {
            lock (gate)
            {
                if (origin < 0 || origin > 2) throw new COMException("Invalid seek.", unchecked((int)0x80030057));
                long next; try { next = checked((origin == 0 ? 0 : origin == 1 ? position : item.Size) + movement); } catch (OverflowException) { throw new COMException("Invalid seek.", unchecked((int)0x80030057)); }
                if (next < 0) throw new COMException("Invalid seek.", unchecked((int)0x80030057));
                position = next; if (result != IntPtr.Zero) Marshal.WriteInt64(result, next);
            }
        }
        public void Stat(out System.Runtime.InteropServices.ComTypes.STATSTG stat, int flags) { stat = new System.Runtime.InteropServices.ComTypes.STATSTG { type = 2, cbSize = item.Size, grfMode = 0, pwcsName = (flags & 1) == 0 ? item.Name : null }; }
        public void Clone(out IStream stream) { lock (gate) stream = new BridgeReadStream(item, copy, owner, sourceThread, token, position); }
        public void CopyTo(IStream destination, long count, IntPtr read, IntPtr written)
        {
            if (count < 0) throw new COMException("Invalid copy count.", unchecked((int)0x80030057));
            byte[] buffer = new byte[81920]; long total = 0; IntPtr amount = Marshal.AllocHGlobal(4);
            try
            {
                while (total < count)
                {
                    Read(buffer, (int)Math.Min(buffer.Length, count - total), amount); int current = Marshal.ReadInt32(amount); if (current == 0) break;
                    destination.Write(buffer, current, amount); if (Marshal.ReadInt32(amount) != current) throw new COMException("Incomplete destination write.", unchecked((int)0x8003001D)); total += current;
                }
            }
            finally { Marshal.FreeHGlobal(amount); if (read != IntPtr.Zero) Marshal.WriteInt64(read, total); if (written != IntPtr.Zero) Marshal.WriteInt64(written, total); }
        }
        private static void ReadOnly() { throw new COMException("Read-only shared file.", unchecked((int)0x80030005)); }
        public void Write(byte[] buffer, int count, IntPtr written) { ReadOnly(); }
        public void SetSize(long size) { ReadOnly(); }
        public void Commit(int flags) { }
        public void Revert() { ReadOnly(); }
        public void LockRegion(long offset, long count, int type) { ReadOnly(); }
        public void UnlockRegion(long offset, long count, int type) { ReadOnly(); }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class BridgeFormats : IEnumFORMATETC
    {
        private readonly FORMATETC[] formats; private int index;
        public BridgeFormats(FORMATETC[] formats) { this.formats = formats; }
        public int Next(int count, FORMATETC[] result, int[] fetched)
        { if (count < 0 || result == null || result.Length < count) return unchecked((int)0x80070057); int n = 0; while (n < count && index < formats.Length) result[n++] = formats[index++]; if (fetched != null && fetched.Length > 0) fetched[0] = n; return n == count ? 0 : 1; }
        public int Skip(int count) { if (count < 0) return unchecked((int)0x80070057); int remain = formats.Length - index; index += Math.Min(count, remain); return count <= remain ? 0 : 1; }
        public int Reset() { index = 0; return 0; }
        public void Clone(out IEnumFORMATETC clone) { clone = new BridgeFormats(formats) { index = index }; }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    internal struct BridgeDescriptor
    {
        public uint Flags; public Guid ClassId; public int Width, Height, X, Y; public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, WriteTime;
        public uint SizeHigh, SizeLow;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    internal static class BridgeOle
    {
        [DllImport("ole32.dll")] internal static extern int OleInitialize(IntPtr reserved);
        [DllImport("ole32.dll")] internal static extern void OleUninitialize();
        [DllImport("ole32.dll")] internal static extern int DoDragDrop([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject data,
            [MarshalAs(UnmanagedType.Interface)] IBridgeDropSource source, int allowed, out int effect);
        [DllImport("ole32.dll")] internal static extern int CoWaitForMultipleHandles(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);
        [DllImport("ole32.dll")] internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll")] internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
        [DllImport("kernel32.dll")] internal static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] internal static extern IntPtr GlobalFree(IntPtr memory);
    }
}
