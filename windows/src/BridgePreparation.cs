using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IPMsg.Dock
{
    internal enum BridgePreparationState { Pending, Preparing, Ready, Failed }

    internal sealed class BridgePreparationStatus
    {
        public BridgePreparationState State { get; internal set; }
        public long Bytes { get; internal set; }
        public long TotalBytes { get; internal set; }
        public string Error { get; internal set; }
    }

    internal sealed class BridgePreparationProgress
    {
        public long Bytes { get; internal set; }
        public long TotalBytes { get; internal set; }
        public int CompletedItems { get; internal set; }
        public int TotalItems { get; internal set; }
    }

    /// <summary>
    /// Prepares local snapshots before a normal Windows FileDrop drag starts.
    /// One session owns at most two downloads, and requests for the same item
    /// version share one task. This class never participates in OLE callbacks.
    /// Completed files belong to the session's temporary-cache owner, which
    /// retains them after retirement while an asynchronous drop may still read.
    /// </summary>
    internal sealed class BridgePreparation : IDisposable
    {
        private sealed class Entry
        {
            internal BridgeItem Item;
            internal CancellationTokenSource Cancellation;
            internal CancellationToken Token;
            internal Task<string> Task;
            internal BridgePreparationState State;
            internal long Bytes, WriteStamp;
            internal string Path, Error;
        }

        private sealed class InlineProgress : IProgress<long>
        {
            private readonly Action<long> report;
            internal InlineProgress(Action<long> action) { report = action; }
            public void Report(long value) { report(value); }
        }

        private readonly object gate = new object();
        private readonly DockBridgeClient client;
        private readonly Func<BridgeItem, string> downloadRootFactory;
        private readonly Func<BridgeItem, string> folderRootFactory;
        private readonly Func<IDisposable> useFactory;
        private readonly CancellationTokenSource session;
        private readonly CancellationToken sessionToken;
        private readonly SemaphoreSlim workers = new SemaphoreSlim(2, 2);
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private Dictionary<string, BridgeItem> available;
        private bool stopped;

        public BridgePreparation(DockBridgeClient bridgeClient, string privateCacheDirectory, CancellationToken token)
            : this(bridgeClient, delegate(BridgeItem item) { return privateCacheDirectory; }, null, token) { }

        public BridgePreparation(DockBridgeClient bridgeClient, Func<string> directoryFactory, CancellationToken token)
            : this(bridgeClient, directoryFactory == null ? null : new Func<BridgeItem, string>(delegate(BridgeItem item) { return directoryFactory(); }), null, token) { }

        public BridgePreparation(DockBridgeClient bridgeClient, Func<BridgeItem, string> directoryFactory,
            Func<IDisposable> acquireCacheUse, CancellationToken token)
            : this(bridgeClient, directoryFactory, null, acquireCacheUse, token) { }

        public BridgePreparation(DockBridgeClient bridgeClient, Func<BridgeItem, string> directoryFactory,
            Func<BridgeItem, string> folderDirectoryFactory, Func<IDisposable> acquireCacheUse, CancellationToken token)
        {
            if (bridgeClient == null) throw new ArgumentNullException("bridgeClient");
            if (directoryFactory == null) throw new ArgumentNullException("directoryFactory");
            client = bridgeClient; downloadRootFactory = directoryFactory; folderRootFactory = folderDirectoryFactory; useFactory = acquireCacheUse;
            session = CancellationTokenSource.CreateLinkedTokenSource(token); sessionToken = session.Token;
        }

        /// <summary>Apply every successful status response, including an empty shelf.</summary>
        public void UpdateItems(IList<BridgeItem> items)
        {
            BridgeItem[] snapshot = Snapshot(items);
            var current = snapshot.ToDictionary(item => item.Id, StringComparer.Ordinal);
            List<Entry> obsolete = new List<Entry>();
            lock (gate)
            {
                if (stopped) return;
                available = current;
                foreach (Entry entry in entries.Values.ToArray())
                {
                    BridgeItem item;
                    if (!current.TryGetValue(entry.Item.Id, out item) || !SameVersion(entry.Item, item))
                    { entries.Remove(entry.Item.Id); obsolete.Add(entry); }
                }
            }
            foreach (Entry entry in obsolete) Cancel(entry);
        }

        public BridgePreparationStatus GetStatus(BridgeItem selected)
        {
            DockBridgeClient.ValidateItem(selected);
            lock (gate)
            {
                if (!Allowed(selected)) return Status(BridgePreparationState.Failed, 0, selected.Size,
                    stopped || sessionToken.IsCancellationRequested ? "Phone file sharing ended." : "The shared file changed. Select it again on the phone.");
                Entry entry;
                if (!entries.TryGetValue(selected.Id, out entry) || !SameVersion(entry.Item, selected))
                    return Status(BridgePreparationState.Pending, 0, selected.Size, null);
                ValidateReady(entry);
                return Status(entry.State, entry.Bytes, entry.Item.Size, entry.Error);
            }
        }

        /// <summary>Only ready local paths are returned; this performs no networking or waits.</summary>
        public bool TryGetReady(IList<BridgeItem> selected, out string[] paths)
        {
            BridgeItem[] items = Snapshot(selected);
            paths = null;
            if (items.Length == 0) return false;
            lock (gate)
            {
                var result = new string[items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    Entry entry;
                    if (!Allowed(items[i]) || !entries.TryGetValue(items[i].Id, out entry) || !SameVersion(entry.Item, items[i])) return false;
                    ValidateReady(entry);
                    if (entry.State != BridgePreparationState.Ready) return false;
                    result[i] = entry.Path;
                }
                paths = result; return true;
            }
        }

        /// <summary>
        /// An explicit request retries a previous failure. Cancelling the caller
        /// stops its wait, while another caller's shared preparation continues.
        /// Shelf invalidation or session cancellation aborts the actual download.
        /// </summary>
        public async Task<string[]> PrepareAsync(IList<BridgeItem> selected,
            IProgress<BridgePreparationProgress> progress, CancellationToken callerToken)
        {
            callerToken.ThrowIfCancellationRequested();
            BridgeItem[] items = Snapshot(selected);
            Entry[] requested = new Entry[items.Length];
            lock (gate)
            {
                sessionToken.ThrowIfCancellationRequested();
                if (stopped) throw new OperationCanceledException("Phone file sharing ended.");
                for (int i = 0; i < items.Length; i++)
                    if (!Allowed(items[i])) throw new IOException("The shared file changed. Select it again on the phone.");
                for (int i = 0; i < items.Length; i++)
                {
                    BridgeItem item = items[i]; Entry entry;
                    if (entries.TryGetValue(item.Id, out entry) && SameVersion(entry.Item, item)) ValidateReady(entry);
                    else entry = null;
                    if (entry == null || entry.State == BridgePreparationState.Failed)
                    {
                        Entry previous;
                        if (entries.TryGetValue(item.Id, out previous)) Cancel(previous);
                        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                        entry = new Entry { Item = item, Cancellation = cancellation, Token = cancellation.Token,
                            State = BridgePreparationState.Pending };
                        entries[item.Id] = entry;
                        Entry captured = entry;
                        entry.Task = Task.Run(delegate { return DownloadAsync(captured); });
                        Observe(entry.Task);
                    }
                    requested[i] = entry;
                }
            }
            Task<string[]> all = Task.WhenAll(requested.Select(entry => entry.Task));
            Observe(all);
            BridgePreparationProgress lastProgress = null;
            using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(callerToken))
            {
                while (!all.IsCompleted && progress != null)
                {
                    lastProgress = Report(progress, requested, lastProgress);
                    await Task.WhenAny(all, Task.Delay(125, cancelled.Token)).ConfigureAwait(false);
                    callerToken.ThrowIfCancellationRequested();
                }
                if (!all.IsCompleted)
                {
                    Task cancellation = Task.Delay(Timeout.Infinite, cancelled.Token);
                    await Task.WhenAny(all, cancellation).ConfigureAwait(false);
                    callerToken.ThrowIfCancellationRequested();
                }
                cancelled.Cancel();
            }
            string[] ready = await all.ConfigureAwait(false);
            callerToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                for (int i = 0; i < items.Length; i++)
                {
                    Entry current;
                    if (!Allowed(items[i]) || !entries.TryGetValue(items[i].Id, out current) || !Object.ReferenceEquals(current, requested[i]))
                        throw new OperationCanceledException("The shared selection changed.");
                    ValidateReady(current);
                    if (current.State != BridgePreparationState.Ready) throw new IOException(current.Error);
                }
            }
            if (progress != null) Report(progress, requested, lastProgress);
            return ready;
        }

        private async Task<string> DownloadAsync(Entry entry)
        {
            bool entered = false;
            try
            {
                await workers.WaitAsync(entry.Token).ConfigureAwait(false); entered = true;
                entry.Token.ThrowIfCancellationRequested();
                lock (gate) { entry.State = BridgePreparationState.Preparing; }
                using (IDisposable use = useFactory == null ? null : useFactory())
                {
                    entry.Token.ThrowIfCancellationRequested();
                    string root;
                    try { root = Path.GetFullPath(downloadRootFactory(entry.Item.Snapshot())); }
                    catch (PathTooLongException) { throw VirtualFileDrag.CachePathTooLong(); }
                    if (root.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Use a local private cache folder.");
                    string path = await client.DownloadAsync(entry.Item, root, new InlineProgress(delegate(long bytes)
                    {
                        lock (gate) { entry.Bytes = Math.Max(entry.Bytes, Math.Min(entry.Item.Size, Math.Max(0, bytes))); }
                    }), entry.Token).ConfigureAwait(false);
                    entry.Token.ThrowIfCancellationRequested();
                    string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    FileInfo file = new FileInfo(path);
                    if (!file.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !file.Exists || file.Length != entry.Item.Size)
                        throw new IOException("The saved copy could not be verified. Fetch it again.");
                    bool folder = FolderSnapshot.IsFolder(entry.Item);
                    if (folder)
                    {
                        try
                        {
                            string folderRoot = folderRootFactory == null ? Path.Combine(root, "folder-" + Guid.NewGuid().ToString("N")) :
                                folderRootFactory(entry.Item.Snapshot());
                            path = FolderSnapshot.Extract(file.FullName, folderRoot, entry.Item.Name, entry.Token);
                        }
                        finally
                        {
                            // A folder's drop source is its verified local tree.
                            // A failed/rejected archive is equally disposable;
                            // repeated retries must not retain duplicate ZIPs.
                            try { File.Delete(file.FullName); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        }
                    }
                    lock (gate)
                    {
                        entry.Token.ThrowIfCancellationRequested();
                        entry.Path = path; entry.WriteStamp = folder ? Directory.GetLastWriteTimeUtc(path).Ticks : file.LastWriteTimeUtc.Ticks;
                        entry.Bytes = entry.Item.Size; entry.State = BridgePreparationState.Ready; entry.Error = null;
                    }
                    return entry.Path;
                }
            }
            catch (OperationCanceledException)
            {
                lock (gate) { entry.State = BridgePreparationState.Failed; entry.Error = "Phone file preparation cancelled."; }
                throw;
            }
            catch (Exception error)
            {
                lock (gate)
                {
                    entry.State = BridgePreparationState.Failed;
                    entry.Error = error is IOException || error is TimeoutException ? error.Message : "Could not fetch the phone file. Reconnect and retry.";
                }
                throw;
            }
            finally
            {
                if (entered) workers.Release();
                // Entry sources are disposed only after their download stops;
                // cancellation racing retirement remains harmless.
                lock (gate) { entry.Cancellation.Dispose(); }
            }
        }

        public void CancelSession()
        {
            lock (gate) { if (stopped) return; stopped = true; }
            try { session.Cancel(); }
            finally { session.Dispose(); }
            // Keep completed paths on disk for the cache owner's grace period.
        }

        public void Dispose() { CancelSession(); }

        private bool Allowed(BridgeItem item)
        {
            if (stopped || sessionToken.IsCancellationRequested) return false;
            if (available == null) return true;
            BridgeItem current;
            return available.TryGetValue(item.Id, out current) && SameVersion(item, current);
        }

        private static void ValidateReady(Entry entry)
        {
            if (entry.State != BridgePreparationState.Ready) return;
            try
            {
                if (FolderSnapshot.IsFolder(entry.Item))
                {
                    FolderSnapshot.RequireSafe(entry.Path);
                    if (Directory.Exists(entry.Path) && Directory.GetLastWriteTimeUtc(entry.Path).Ticks == entry.WriteStamp) return;
                    throw new IOException("The prepared folder changed.");
                }
                FileInfo file = new FileInfo(entry.Path);
                if (file.Exists && file.Length == entry.Item.Size && file.LastWriteTimeUtc.Ticks == entry.WriteStamp) return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            entry.State = BridgePreparationState.Failed; entry.Bytes = 0;
            entry.Error = "The fetched copy changed or was removed. Fetch it again.";
        }

        private BridgePreparationProgress Report(IProgress<BridgePreparationProgress> progress, Entry[] requested,
            BridgePreparationProgress previous)
        {
            BridgePreparationProgress snapshot;
            lock (gate)
            {
                snapshot = new BridgePreparationProgress { Bytes = requested.Sum(entry => entry.Bytes),
                    TotalBytes = requested.Sum(entry => entry.Item.Size), TotalItems = requested.Length,
                    CompletedItems = requested.Count(entry => entry.State == BridgePreparationState.Ready) };
            }
            // Queued items can wait behind large files. Do not flood the UI
            // context with identical progress callbacks for all thirty cards.
            if (previous == null || previous.Bytes != snapshot.Bytes || previous.TotalBytes != snapshot.TotalBytes ||
                previous.CompletedItems != snapshot.CompletedItems || previous.TotalItems != snapshot.TotalItems)
                progress.Report(snapshot);
            return snapshot;
        }

        private static BridgePreparationStatus Status(BridgePreparationState state, long bytes, long total, string error)
        { return new BridgePreparationStatus { State = state, Bytes = bytes, TotalBytes = total, Error = error }; }

        private static BridgeItem[] Snapshot(IList<BridgeItem> items)
        {
            if (items == null) throw new ArgumentNullException("items");
            if (items.Count > 30) throw new ArgumentException("Select up to 30 phone files.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            return items.Select(delegate(BridgeItem item)
            {
                DockBridgeClient.ValidateItem(item);
                if (!ids.Add(item.Id)) throw new ArgumentException("The same phone file was selected more than once.");
                return item.Snapshot();
            }).ToArray();
        }

        private static bool SameVersion(BridgeItem left, BridgeItem right)
        {
            return left.Id == right.Id && left.Name == right.Name && left.Size == right.Size &&
                left.Modified == right.Modified && left.Mime == right.Mime;
        }

        private static void Cancel(Entry entry)
        { try { entry.Cancellation.Cancel(); } catch (ObjectDisposedException) { } }

        private static void Observe(Task task)
        {
            task.ContinueWith(delegate(Task failed) { var observed = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
