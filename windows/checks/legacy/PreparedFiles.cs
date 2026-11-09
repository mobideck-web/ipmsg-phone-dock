using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace IPMsg.Dock
{
    /// <summary>
    /// Session-local index of explicitly prepared file copies. Construction,
    /// selection and lookup never contact a phone. Only PrepareAsync downloads.
    /// </summary>
    public sealed class StagedFiles
    {
        private readonly FileTransferService transfers;
        private readonly string stateRoot;
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public StagedFiles(FileTransferService transfers, string stateDirectory)
        {
            if (transfers == null) throw new ArgumentNullException("transfers");
            if (String.IsNullOrWhiteSpace(stateDirectory)) throw new ArgumentException("A state directory is required.");
            this.transfers = transfers;
            // Path normalization only: do not create/read directories or probe a
            // device until the user explicitly requests preparation.
            stateRoot = Path.GetFullPath(stateDirectory).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        public bool TryGet(string serial, PhoneFile file, out string localPath)
        {
            localPath = null;
            if (String.IsNullOrWhiteSpace(serial) || file == null || file.IsDirectory ||
                file.Size < 0 || String.IsNullOrWhiteSpace(file.FullPath)) return false;
            string key = Key(serial, file);
            Entry entry;
            ReadyCopy ready;
            lock (gate)
            {
                if (!entries.TryGetValue(key, out entry) || entry.Ready == null) return false;
                ready = entry.Ready;
            }
            try
            {
                var info = new FileInfo(ready.Path);
                if (info.Exists && info.Length == ready.Size && info.LastWriteTimeUtc.Ticks == ready.ModifiedTicks)
                {
                    localPath = ready.Path;
                    return true;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            Remove(key, entry);
            return false;
        }

        /// <summary>
        /// One underlying download per identity/version. The first caller owns
        /// that download's cancellation; later callers can cancel their own
        /// wait without cancelling another caller's transfer. Progress belongs
        /// to the first caller. Refreshing a phone listing changes the key when
        /// its size or modification time changes.
        /// </summary>
        public Task<string> PrepareAsync(string serial, PhoneFile file,
            IProgress<TransferProgress> progress, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(serial)) throw new ArgumentException("Choose a connected phone first.");
            if (file == null) throw new ArgumentNullException("file");
            if (file.IsDirectory || file.Size < 0 || String.IsNullOrWhiteSpace(file.FullPath))
                throw new IOException("Select individual phone files and refresh their listing before preparing.");
            token.ThrowIfCancellationRequested();
            string ready;
            if (TryGet(serial, file, out ready)) return Task.FromResult(ready);

            // Snapshot the metadata; a refreshed UI must not mutate an active
            // transfer's source identity or the backend's change checks.
            PhoneFile selected = Snapshot(file);
            string key = Key(serial, selected);
            Entry entry;
            bool owner = false;
            lock (gate)
            {
                if (!entries.TryGetValue(key, out entry))
                {
                    entry = new Entry();
                    entries.Add(key, entry);
                    owner = true;
                }
            }
            if (owner)
            {
                // All failures are captured in the entry's completion task.
                Task producer = PrepareOwnedAsync(key, entry, serial, selected, progress, token);
                entry.Producer = producer;
                return entry.Completion.Task;
            }
            return WaitForAsync(entry.Completion.Task, token);
        }

        private async Task PrepareOwnedAsync(string key, Entry entry, string serial,
            PhoneFile file, IProgress<TransferProgress> progress, CancellationToken token)
        {
            try
            {
                string path = await transfers.DownloadAsync(serial, file, progress, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                path = Path.GetFullPath(path);
                if (!path.StartsWith(stateRoot, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The prepared file is outside the dock's local staging directory.");
                ReadyCopy ready;
                // The backend verifies the phone's size and modification time
                // before and after the copy. Record the completed local copy's
                // stamp so local edits/deletion invalidate later lookups.
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (held.Length != file.Size) throw new IOException("The prepared file is incomplete. Prepare it again.");
                    ready = new ReadyCopy(path, held.Length, File.GetLastWriteTimeUtc(path).Ticks);
                }
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    Entry current;
                    if (entries.TryGetValue(key, out current) && Object.ReferenceEquals(current, entry))
                        entry.Ready = ready;
                }
                entry.Completion.TrySetResult(path);
            }
            catch (OperationCanceledException)
            {
                Remove(key, entry);
                entry.Completion.TrySetCanceled();
            }
            catch (Exception error)
            {
                Remove(key, entry);
                entry.Completion.TrySetException(error);
            }
        }

        private static async Task<string> WaitForAsync(Task<string> download, CancellationToken token)
        {
            if (!token.CanBeCanceled) return await download.ConfigureAwait(false);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(delegate { cancelled.TrySetResult(true); }))
            {
                await Task.WhenAny(download, cancelled.Task).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return await download.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Forget readiness without deleting files: Windows drop targets can
        /// continue reading them after DoDragDrop returns. This does not cancel
        /// another caller's active download; its owner must cancel its token.
        /// </summary>
        public void Clear() { lock (gate) entries.Clear(); }

        private void Remove(string key, Entry expected)
        {
            lock (gate)
            {
                Entry current;
                if (entries.TryGetValue(key, out current) && Object.ReferenceEquals(current, expected)) entries.Remove(key);
            }
        }

        private static PhoneFile Snapshot(PhoneFile file)
        {
            return new PhoneFile { Name = file.Name, FullPath = file.FullPath, Size = file.Size,
                IsDirectory = false, ModifiedUtc = Utc(file.ModifiedUtc) };
        }

        private static DateTime Utc(DateTime value)
        {
            if (value == DateTime.MinValue) return DateTime.MinValue;
            return value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static string Key(string serial, PhoneFile file)
        {
            return serial.Length.ToString(CultureInfo.InvariantCulture) + ":" + serial +
                file.FullPath.Length.ToString(CultureInfo.InvariantCulture) + ":" + file.FullPath + ":" +
                file.Size.ToString(CultureInfo.InvariantCulture) + ":" + Utc(file.ModifiedUtc).Ticks.ToString(CultureInfo.InvariantCulture);
        }

        private sealed class Entry
        {
            public readonly TaskCompletionSource<string> Completion =
                new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            public ReadyCopy Ready;
            public Task Producer;
        }

        private sealed class ReadyCopy
        {
            public readonly string Path;
            public readonly long Size, ModifiedTicks;
            public ReadyCopy(string path, long size, long modifiedTicks)
            { Path = path; Size = size; ModifiedTicks = modifiedTicks; }
        }
    }
}
