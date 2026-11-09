using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IPMsg.Dock
{
    internal sealed partial class DockForm
    {
        private DockBridgeClient phoneBridge;
        private BridgePreparation bridgePreparation;
        private TemporaryBridgeCache temporaryCache;
        private readonly HashSet<string> preparationRequested = new HashSet<string>(StringComparer.Ordinal);
        private int cachePruning;
        private System.Threading.Timer cacheMaintenance;
        private DateTime lastCachePrune = DateTime.MinValue;
        private CancellationTokenSource bridgeLifetime;
        private readonly SemaphoreSlim bridgeGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim thumbnailGate = new SemaphoreSlim(1, 1); // Leave a phone worker free for Stop/status during two-file copies.
        private readonly ImageList thumbnails = new ImageList();
        private readonly HashSet<string> thumbnailRequests = new HashSet<string>(StringComparer.Ordinal);
        private List<BridgeItem> sharedItems = new List<BridgeItem>();
        private BridgeGesture phoneGesture;
        private string bridgeSerial, bridgeToken;
        private int bridgePort, bridgeGeneration, bridgeFailures;
        private long shelfRevision = -1, consumedGesture = -1;
        private bool bridgePolling, bridgeOpening, bridgeApproved, bridgeUiDisposed, shelfHidden;
        private Task bridgeCleanup;
        private TaskCompletionSource<bool> bridgeOpeningFinished;
        private DateTime gestureSeen, lastBridgePoll = DateTime.MinValue;

        private void InitializeBridgeShelf()
        {
            thumbnails.ImageSize = new Size(80, 80); thumbnails.ColorDepth = ColorDepth.Depth32Bit;
            IntPtr imageHandle = thumbnails.Handle; // Copy images now, before their temporary Bitmaps are disposed.
            using (Bitmap icon = FilePreview()) thumbnails.Images.Add("file", icon);
            files.ConfigureForDock(thumbnails); files.MultiSelect = true;
            files.HideSelection = false; files.AutoArrange = true; files.LabelWrap = true;
            files.ShowItemToolTips = true; files.AccessibleName = "Shared phone files and folders";
            files.ItemDrag += delegate { DragShared(SelectedSharedItems()); };
            files.KeyDown += async delegate(object sender, KeyEventArgs args)
            { if (args.KeyCode == Keys.Enter) { args.SuppressKeyPress = true; await SaveSharedAsync(); } };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Save to PC…", null, async delegate { await SaveSharedAsync(); });
            var retry = menu.Items.Add("Retry preparation", null, delegate { PrepareSharedSelection(SelectedSharedItems()); });
            menu.Items.Add("Choose on phone…", null, async delegate { await OpenBridgeViewAsync("files"); });
            menu.Items.Add("Clear tray", null, delegate { ClearTemporaryShelf(); });
            menu.Opening += delegate { retry.Visible = bridgePreparation != null && SelectedSharedItems().Any(item => bridgePreparation.GetStatus(item).State == BridgePreparationState.Failed); };
            files.ContextMenuStrip = menu;
            pathLabel.Text = "Temporary files";
        }

        private static Bitmap FilePreview()
        {
            Bitmap image = new Bitmap(80, 80);
            using (Graphics graphics = Graphics.FromImage(image))
            using (Pen pen = new Pen(GlassTheme.MutedText, 2))
            {
                graphics.Clear(GlassTheme.Card); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.DrawRectangle(pen, 22, 12, 36, 54);
                graphics.DrawLine(pen, 30, 34, 50, 34); graphics.DrawLine(pen, 30, 43, 50, 43); graphics.DrawLine(pen, 30, 52, 44, 52);
            }
            return image;
        }
        private List<BridgeItem> SelectedSharedItems()
        { return files.SelectedItems.Cast<ListViewItem>().Select(item => item.Tag as BridgeItem).Where(item => item != null).Select(item => item.Snapshot()).ToList(); }

        private async Task ToggleFilesShelfAsync()
        {
            if (!CanTransfer()) return;
            if (phoneBridge != null && sharedItems.Count > 0) { SetShelfVisible(!trayOpen); return; }
            shelfHidden = false; await OpenBridgeViewAsync("files");
        }
        private void SetShelfVisible(bool show)
        {
            shelfHidden = !show; trayOpen = show && sharedItems.Count > 0;
            ApplyChrome();
        }
        private void ClearTemporaryShelf()
        {
            CancelOperation(); StopBridge(); ApplyChrome();
            SetStatus("Tray cleared. Temporary copies expire automatically.");
        }

        private void QueueCachePrune()
        {
            if (Interlocked.CompareExchange(ref cachePruning, 1, 0) != 0) return;
            if ((DateTime.UtcNow - lastCachePrune).TotalMinutes < 5) { Interlocked.Exchange(ref cachePruning, 0); return; }
            lastCachePrune = DateTime.UtcNow;
            Task task = Task.Run(delegate { try { TemporaryBridgeCache.Prune(stateDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { } });
            task.ContinueWith(delegate(Task completed) { if (completed.IsFaulted) { var ignored = completed.Exception; } Interlocked.Exchange(ref cachePruning, 0); }, TaskScheduler.Default);
        }
        private void InitializeCacheMaintenance()
        {
            QueueCachePrune();
            cacheMaintenance = new System.Threading.Timer(delegate { QueueCachePrune(); }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        private async Task OpenBridgeViewAsync(string modeName)
        {
            if (modeName != "files" && modeName != "edge") throw new ArgumentException("Unknown phone view.");
            if (!CanTransfer() || bridgeOpening) return;
            shelfHidden = false;
            bridgeOpening = true; bool entered = false;
            bridgeOpeningFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await bridgeGate.WaitAsync(lifetime.Token); entered = true;
                string sourceSerial = serial;
                if (!session.IsRunning || String.IsNullOrEmpty(sourceSerial)) return;
                if (phoneBridge == null)
                {
                    bridgeLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    CancellationToken openingToken = bridgeLifetime.Token;
                    string openingSecret = NewBridgeToken();
                    bridgeSerial = sourceSerial; bridgeToken = openingSecret;
                    // Finish this short atomic ADB operation even if the view closes, so its
                    // assigned port is known and can be removed. Nothing is shared yet.
                    CommandResult forwarded;
                    using (var forwardLimit = new CancellationTokenSource(5000))
                        forwarded = await bridge.RunAsync(new[] { "-s", sourceSerial, "forward", "tcp:0", "tcp:2428" }, forwardLimit.Token, 5000);
                    int port;
                    if (forwarded.ExitCode != 0 || forwarded.OutputTruncated || !Regex.IsMatch(forwarded.Output.Trim(), "^[0-9]{1,5}$") ||
                        !Int32.TryParse(forwarded.Output.Trim(), out port) || port < 1 || port > 65535)
                        throw new IOException("Could not create the private phone connection. Reconnect USB or Wi-Fi.");
                    if (openingToken.IsCancellationRequested || sourceSerial != serial || !session.IsRunning)
                    {
                        bridgeCleanup = CleanupBridgeAsync(new DockBridgeClient(port, openingSecret), sourceSerial, port);
                        ObserveBridgeTask(bridgeCleanup); throw new OperationCanceledException();
                    }
                    bridgePort = port; phoneBridge = new DockBridgeClient(port, openingSecret);
                    DockBridgeClient createdClient = phoneBridge;
                    TemporaryBridgeCache createdCache = await Task.Run(delegate { return TemporaryBridgeCache.CreateSession(stateDirectory); }, openingToken);
                    if (openingToken.IsCancellationRequested || createdClient != phoneBridge || !session.IsRunning)
                    { ObserveBridgeTask(Task.Run(delegate { createdCache.Retire(); })); throw new OperationCanceledException(); }
                    temporaryCache = createdCache;
                    TemporaryBridgeCache ownedCache = temporaryCache;
                    bridgePreparation = new BridgePreparation(phoneBridge, item => ownedCache.CreateDownloadDirectory(item.Name), item => ownedCache.CreateFolderDirectory(item.Name), ownedCache.BeginUse, bridgeLifetime.Token);
                    preparationRequested.Clear();
                    shelfRevision = consumedGesture = -1; bridgeFailures = 0; bridgeApproved = false;
                }
                DockBridgeClient selected = phoneBridge;
                string script = "am start -n app.lantern.lan/.DockFilesActivity --es dock_session " + AdbBridge.ShellQuote(bridgeToken) +
                    " --es dock_mode " + AdbBridge.ShellQuote(modeName);
                SetStatus(bridgeApproved ? "Opening phone files…" : "Approve file access on the phone.");
                CommandResult opened = await bridge.ShellAsync(sourceSerial, script, bridgeLifetime.Token, 10000);
                if (opened.ExitCode != 0 || opened.Output.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    opened.Error.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new IOException("Install the updated IPMsg Android app, then open Files & photos again.");
                if (selected == phoneBridge) { trayOpen = sharedItems.Count > 0; ApplyChrome(); lastBridgePoll = DateTime.MinValue; }
            }
            catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) SetStatus("Phone files cancelled."); }
            catch (Exception error) { StopBridge(); if (!lifetime.IsCancellationRequested) SetStatus(error is IOException ? error.Message : "Could not open phone files. Reconnect and retry.", true); }
            finally { bridgeOpening = false; if (entered) bridgeGate.Release(); bridgeOpeningFinished.TrySetResult(true); if (!IsDisposed) UpdateControls(); }
        }

        private static string NewBridgeToken()
        {
            byte[] bytes = new byte[32]; using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }
        private void TickBridge()
        {
            CheckPhoneDrag();
            if (phoneBridge != null && !bridgePolling && (DateTime.UtcNow - lastBridgePoll).TotalMilliseconds >= 140) PollBridgeAsync();
        }
        private async void PollBridgeAsync()
        {
            DockBridgeClient selected = phoneBridge; CancellationTokenSource scope = bridgeLifetime;
            if (selected == null || scope == null || bridgePolling || bridgeUiDisposed) return;
            int generation = bridgeGeneration; bridgePolling = true; lastBridgePoll = DateTime.UtcNow;
            try
            {
                BridgeStatus state = await selected.GetStatusAsync(scope.Token);
                if (generation != bridgeGeneration || selected != phoneBridge || IsDisposed || bridgeUiDisposed) return;
                if (!bridgeApproved) SetStatus("Phone files linked · select on the phone, then drag to your PC.");
                bridgeApproved = true; bridgeFailures = 0;
                // Permission/version changes take effect even while a Save dialog
                // or native drag preserves the existing visual selection.
                if (bridgePreparation != null) bridgePreparation.UpdateItems(state.Items);
                if (state.Revision != shelfRevision && !preparing) { UpdateShelf(state.Items); shelfRevision = state.Revision; }
                if (state.Gesture.Revision != (phoneGesture == null ? -1 : phoneGesture.Revision)) gestureSeen = DateTime.UtcNow;
                phoneGesture = state.Gesture;
                foreach (BridgeItem item in sharedItems.Where(item => item.Thumbnail && !thumbnails.Images.ContainsKey(item.Id)))
                    if (thumbnailRequests.Add(item.Id)) { var ignored = LoadThumbnailAsync(selected, item.Snapshot(), generation, scope.Token); }
                CheckPhoneDrag();
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (generation == bridgeGeneration && selected == phoneBridge && !IsDisposed)
                {
                    bridgeFailures++;
                    if (bridgeApproved && bridgeFailures >= 3) { StopBridge(); ApplyChrome(); SetStatus("File sharing ended. Open Files & photos to reconnect."); }
                }
            }
            finally { bridgePolling = false; }
        }

        private void UpdateShelf(List<BridgeItem> items)
        {
            if (preparing) return; // Preserve the selected source/version while a drag or Save owns it.
            HashSet<string> selected = new HashSet<string>(SelectedSharedItems().Select(item => item.Id), StringComparer.Ordinal);
            sharedItems = items.Select(item => item.Snapshot()).ToList();
            if (bridgePreparation != null) bridgePreparation.UpdateItems(sharedItems);
            HashSet<string> current = new HashSet<string>(sharedItems.Select(item => item.Id), StringComparer.Ordinal);
            var currentVersions = new HashSet<string>(sharedItems.Select(PreparationKey), StringComparer.Ordinal);
            preparationRequested.RemoveWhere(key => !currentVersions.Contains(key));
            foreach (string key in thumbnails.Images.Keys.Cast<string>().Where(key => key != "file" && !current.Contains(key)).ToArray()) thumbnails.Images.RemoveByKey(key);
            thumbnailRequests.RemoveWhere(id => !current.Contains(id));
            files.BeginUpdate();
            try
            {
                files.Items.Clear();
                foreach (BridgeItem item in sharedItems)
                {
                    string displayName = FolderSnapshot.DisplayName(item);
                    var row = new ListViewItem(displayName + "\n" + Bytes(item.Size)) { Name = item.Id, Tag = item,
                        ImageKey = thumbnails.Images.ContainsKey(item.Id) ? item.Id : "file", ToolTipText = displayName + " · " + Bytes(item.Size) };
                    files.Items.Add(row); row.Selected = selected.Contains(item.Id);
                }
            }
            finally { files.EndUpdate(); }
            trayOpen = sharedItems.Count > 0 && !shelfHidden; pathLabel.Text = sharedItems.Count + (sharedItems.Count == 1 ? " item · temporary" : " items · temporary"); ApplyChrome();
            foreach (BridgeItem item in sharedItems) StartPreparation(item, false);
        }
        private async Task LoadThumbnailAsync(DockBridgeClient selected, BridgeItem item, int generation, CancellationToken token)
        {
            bool entered = false;
            try
            {
                await thumbnailGate.WaitAsync(token); entered = true;
                byte[] bytes = await selected.GetThumbnailAsync(item.Id, token);
                using (Bitmap scaled = await Task.Run(delegate
                {
                    using (var stream = new MemoryStream(bytes, false))
                    using (Image image = Image.FromStream(stream, false, true))
                    {
                    Bitmap bitmap = new Bitmap(80, 80);
                    try
                    {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Card); graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        double ratio = Math.Min(80.0 / image.Width, 80.0 / image.Height); int width = Math.Max(1, (int)(image.Width * ratio)), height = Math.Max(1, (int)(image.Height * ratio));
                        graphics.DrawImage(image, new Rectangle((80 - width) / 2, (80 - height) / 2, width, height));
                    }
                    return bitmap;
                    }
                    catch { bitmap.Dispose(); throw; }
                    }
                }, token))
                {
                    if (generation == bridgeGeneration && selected == phoneBridge && !IsDisposed && !bridgeUiDisposed && sharedItems.Any(file => file.Id == item.Id))
                    {
                        if (!thumbnails.Images.ContainsKey(item.Id)) thumbnails.Images.Add(item.Id, scaled);
                        ListViewItem row = files.Items[item.Id]; if (row != null) row.ImageKey = item.Id;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            finally { if (entered) thumbnailGate.Release(); }
        }

        private void CheckPhoneDrag()
        {
            if (phoneGesture == null || phoneGesture.Phase != "start" || phoneGesture.Revision == consumedGesture ||
                preparing || busy || connecting || bridgeOpening || !bridgeApproved || (DateTime.UtcNow - gestureSeen).TotalSeconds > 12 ||
                (Control.MouseButtons & MouseButtons.Left) == 0 || !Visible || fullscreen) return;
            Rectangle view = preview.RectangleToScreen(preview.ClientRectangle); Point pointer = Control.MousePosition;
            if (view.Contains(pointer)) return;
            List<BridgeItem> selected = phoneGesture.ItemIds.Select(id => sharedItems.FirstOrDefault(item => item.Id == id)).Where(item => item != null).ToList();
            if (selected.Count != phoneGesture.ItemIds.Count || selected.Count == 0) return;
            consumedGesture = phoneGesture.Revision;
            DragShared(selected);
        }
        private static string PreparationKey(BridgeItem item)
        { return item.Id + ":" + item.Size + ":" + item.Modified + ":" + item.Name + ":" + item.Mime; }

        private void PrepareSharedSelection(List<BridgeItem> items)
        {
            if (!CanTransfer() || bridgePreparation == null || !bridgeApproved) return;
            foreach (BridgeItem item in items) StartPreparation(item, true);
            if (items.Count > 0) SetStatus("Preparing selected files…");
        }
        private void StartPreparation(BridgeItem item, bool retry)
        {
            if (bridgePreparation == null || bridgeLifetime == null || bridgeLifetime.IsCancellationRequested) return;
            string key = PreparationKey(item);
            if (!preparationRequested.Add(key) && !retry) { RefreshPreparation(item, bridgePreparation); return; }
            var ignored = PrepareItemAsync(item.Snapshot(), bridgePreparation, bridgeGeneration, bridgeLifetime.Token);
            ObserveBridgeTask(ignored);
        }
        private async Task PrepareItemAsync(BridgeItem item, BridgePreparation selected, int generation, CancellationToken token)
        {
            var progressUpdate = new Progress<BridgePreparationProgress>(delegate
            {
                if (generation == bridgeGeneration && selected == bridgePreparation && !IsDisposed && !token.IsCancellationRequested)
                    RefreshPreparation(item, selected);
            });
            try { await selected.PrepareAsync(new[] { item }, progressUpdate, token); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* The card retains the actionable preparation failure. */ }
            finally
            {
                if (generation == bridgeGeneration && selected == bridgePreparation && !IsDisposed && !token.IsCancellationRequested)
                    RefreshPreparation(item, selected);
            }
        }
        private void RefreshPreparation(BridgeItem item, BridgePreparation selected)
        {
            if (selected != bridgePreparation || bridgeUiDisposed || IsDisposed || !sharedItems.Any(current => PreparationKey(current) == PreparationKey(item))) return;
            BridgePreparationStatus state = selected.GetStatus(item);
            int percent = state.State == BridgePreparationState.Ready ? 100 : state.TotalBytes == 0 ? 0 : (int)Math.Min(99, state.Bytes * 100.0 / state.TotalBytes);
            files.SetPreparation(item.Id, percent, state.State == BridgePreparationState.Ready, state.Error);
        }

        private void DragShared(List<BridgeItem> items)
        {
            if (!CanTransfer() || !bridgeApproved || phoneBridge == null || bridgePreparation == null || temporaryCache == null || items.Count == 0 || (Control.MouseButtons & MouseButtons.Left) == 0) return;
            string[] paths;
            if (!bridgePreparation.TryGetReady(items, out paths))
            {
                foreach (BridgeItem item in items) StartPreparation(item, true);
                SetStatus("Preparing files… Drag them when the cards are ready.");
                return;
            }
            DockBridgeClient selected = phoneBridge; TemporaryBridgeCache ownedCache = temporaryCache;
            IDisposable sourceLease;
            if (!ownedCache.TryBeginUse(out sourceLease)) { SetStatus("Finishing preparation… Drag again in a moment."); return; }
            preparing = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, bridgeLifetime.Token); cancel.Visible = true;
            CancellationToken token = operation.Token;
            try
            {
                BridgeGesture gesture = phoneGesture;
                if (gesture != null && gesture.Phase == "start" && new HashSet<string>(items.Select(item => item.Id), StringComparer.Ordinal).SetEquals(gesture.ItemIds))
                {
                    // Capture before dispatch. A delayed acknowledgement must never
                    // clear the next gesture that arrives while Windows is copying.
                    string gestureId = gesture.Id; long revision = gesture.Revision;
                    ObserveBridgeTask(Task.Run(delegate { return selected.EndGestureAsync(gestureId, revision, token); }));
                }
                // Enter Windows' own drag loop in the original mouse event. Every
                // path is already complete: no process startup, pipe, HTTP or custom
                // IStream can delay the receiver's OLE callbacks.
                SetStatus("Drag to a Windows folder.");
                DragDropEffects effect = VirtualFileDrag.StartLocal(this, paths, token);
                if (selected == phoneBridge && !token.IsCancellationRequested && !IsDisposed)
                    SetStatus((effect & DragDropEffects.Copy) != 0 ? "Files handed to Windows." : "Drag cancelled. Your files are still available.");
            }
            catch (OperationCanceledException) { if (selected == phoneBridge && !IsDisposed) SetStatus("Copy cancelled."); }
            catch (Exception) { if (selected == phoneBridge && !token.IsCancellationRequested && !IsDisposed) SetStatus("Copy could not finish. Retry or choose Save to PC.", true); }
            finally { ObserveBridgeTask(Task.Run(delegate { sourceLease.Dispose(); })); preparing = false; shelfRevision = -1; EndOperation(); }
        }
        private async Task SaveSharedAsync()
        {
            if (!CanTransfer() || phoneBridge == null || bridgePreparation == null || temporaryCache == null || !bridgeApproved) return;
            List<BridgeItem> items = SelectedSharedItems(); if (items.Count == 0) { SetStatus("Select a shared item first."); return; }
            DockBridgeClient selected = phoneBridge; BridgePreparation preparation = bridgePreparation; TemporaryBridgeCache ownedCache = temporaryCache;
            preparing = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, bridgeLifetime.Token); CancellationToken token = operation.Token; UpdateControls();
            cancel.Text = "Cancel save"; cancel.AccessibleDescription = "Cancel this save. Clear tray ends all background preparation.";
            try
            {
                string destination;
                using (var picker = new FolderBrowserDialog { Description = "Save shared phone files on this PC", ShowNewFolderButton = true })
                { if (picker.ShowDialog(this) != DialogResult.OK) return; destination = picker.SelectedPath; }
                foreach (BridgeItem item in items)
                {
                    string[] paths = await preparation.PrepareAsync(new[] { item }, new Progress<BridgePreparationProgress>(update =>
                    {
                        if (IsDisposed || selected != phoneBridge || token.IsCancellationRequested) return; progress.Value = item.Size == 0 ? 0 : (int)Math.Min(1000, update.Bytes * 1000.0 / item.Size);
                        RefreshPreparation(item, preparation); SetStatus("Preparing " + FolderSnapshot.DisplayName(item) + "…");
                    }), token);
                    Task copy = Task.Run(delegate
                    { using (ownedCache.BeginUse()) { token.ThrowIfCancellationRequested(); if (FolderSnapshot.IsFolder(item)) FolderSnapshot.CopyUnique(paths[0], destination, token); else CopyUnique(paths[0], destination, token); } }, token);
                    await AwaitCancelableFileWorkAsync(copy, token);
                }
                if (selected == phoneBridge && !token.IsCancellationRequested && !IsDisposed) SetStatus(items.Count == 1 ? "Saved on this PC." : items.Count + " items saved on this PC.");
            }
            catch (OperationCanceledException) { if (selected == phoneBridge && !IsDisposed) SetStatus("Save cancelled. Files remain in the temporary tray."); }
            catch (Exception error) { if (selected == phoneBridge && !token.IsCancellationRequested && !IsDisposed) SetStatus(error is IOException ? error.Message : "Could not save the file. Check the connection and destination.", true); }
            finally { cancel.Text = "Cancel"; cancel.AccessibleDescription = null; preparing = false; shelfRevision = -1; EndOperation(); }
        }

        private void StopBridge()
        {
            DockBridgeClient selected = phoneBridge; string sourceSerial = bridgeSerial; int port = bridgePort;
            BridgePreparation oldPreparation = bridgePreparation; TemporaryBridgeCache oldCache = temporaryCache;
            bridgePreparation = null; temporaryCache = null; preparationRequested.Clear();
            if (oldPreparation != null) oldPreparation.Dispose();
            if (oldCache != null) ObserveBridgeTask(Task.Run(delegate { try { oldCache.Retire(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }));
            bridgeGeneration++; phoneBridge = null; bridgeToken = bridgeSerial = null; bridgePort = 0;
            bridgeApproved = false; phoneGesture = null; sharedItems.Clear(); thumbnailRequests.Clear(); shelfRevision = consumedGesture = -1;
            if (bridgeLifetime != null) { bridgeLifetime.Cancel(); bridgeLifetime.Dispose(); bridgeLifetime = null; }
            if (!bridgeUiDisposed) { files.Items.Clear(); files.ClearPreparation(); thumbnails.Images.Clear(); using (Bitmap icon = FilePreview()) thumbnails.Images.Add("file", icon); }
            trayOpen = false; shelfHidden = false;
            if (selected != null && !String.IsNullOrEmpty(sourceSerial)) { bridgeCleanup = CleanupBridgeAsync(selected, sourceSerial, port); ObserveBridgeTask(bridgeCleanup); }
        }
        private async Task CleanupBridgeAsync(DockBridgeClient selected, string sourceSerial, int port)
        {
            using (var limit = new CancellationTokenSource(1500))
            {
                try { await selected.StopAsync(limit.Token).ConfigureAwait(false); } catch (Exception) { }
            }
            if (bridgePort == port && bridgeSerial == sourceSerial) return;
            // Removing the owned tunnel must still run if the phone ignored /stop.
            using (var removal = new CancellationTokenSource(3000))
                try { await bridge.RunAsync(new[] { "-s", sourceSerial, "forward", "--remove", "tcp:" + port }, removal.Token, 3000).ConfigureAwait(false); } catch (Exception) { }
        }
        private static void ObserveBridgeTask(Task task)
        { task.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
        private async Task AwaitBridgeCleanupAsync()
        { await Task.WhenAny(FinishBridgeCleanupAsync(), Task.Delay(10500)); }
        private async Task FinishBridgeCleanupAsync()
        {
            TaskCompletionSource<bool> opening = bridgeOpeningFinished;
            if (opening != null) await opening.Task;
            Task cleanup = bridgeCleanup; if (cleanup != null) await cleanup;
        }
        private void DisposeBridgeUi()
        { if (bridgeUiDisposed) return; bridgeUiDisposed = true; if (cacheMaintenance != null) { cacheMaintenance.Dispose(); cacheMaintenance = null; } thumbnails.Dispose(); }
    }
}
