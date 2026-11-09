using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;
using IPMsg.Dock;

// Isolated protocol-only doubles: no adb, phone, window or network is started.
// Compile with the actual VirtualFileDrag.cs and PreparedFiles.cs under review.
namespace IPMsg.Dock
{
    public sealed class PhoneFile
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public long Size { get; set; }
        public bool IsDirectory { get; set; }
        public DateTime ModifiedUtc { get; set; }
    }
    public sealed class TransferProgress { }
    public sealed class FileTransferService
    {
        public Func<string, PhoneFile, CancellationToken, Task<string>> Download;
        public Task<string> DownloadAsync(string serial, PhoneFile file,
            IProgress<TransferProgress> progress, CancellationToken token)
        { return Download(serial, file, token); }
    }
}

internal static class DragRegression
{
    private static int passed, failed;
    private static string fixtures, empty;
    private static readonly List<string> owned = new List<string>();

    [STAThread]
    private static int Main()
    {
        int ole = OleInitialize(IntPtr.Zero);
        if (ole < 0) Marshal.ThrowExceptionForHR(ole);
        fixtures = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "drag-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        empty = Path.Combine(fixtures, "empty-文件-é.txt");
        File.WriteAllBytes(empty, new byte[0]); owned.Add(empty);
        try
        {
            Check("Native CF_HDROP preserves Unicode and zero-byte file", delegate
            {
                DataObject data = VirtualFileDrag.CreateLocalDataObject(new[] { empty });
                Assert(data.GetFileDropList().Count == 1, "Expected one ordinary file drop.");
                Assert(!data.GetDataPresent("FileContents", false), "Virtual file callback must not be advertised.");
                Assert(!data.GetDataPresent("FileGroupDescriptorW", false), "Virtual descriptors must not be advertised.");
                AssertNativePaths(data, new[] { empty });
            });
            Check("Repeated local path appears only once", delegate
            {
                AssertNativePaths(VirtualFileDrag.CreateLocalDataObject(new[] { empty, empty }), new[] { empty });
            });
            Check("Missing local copy is rejected before starting OLE", delegate
            {
                Expect<FileNotFoundException>(delegate { VirtualFileDrag.CreateLocalDataObject(new[] { Path.Combine(fixtures, "missing") }); });
            });
            Check("UNC paths are rejected without probing a share", delegate
            {
                Expect<IOException>(delegate { VirtualFileDrag.CreateLocalDataObject(new[] { @"\\not-a-real-server\share\file" }); });
            });
            Check("Pre-cancelled and empty drags return without creating a window", delegate
            {
                using (var owner = new Control())
                using (var token = new CancellationTokenSource())
                {
                    token.Cancel();
                    Assert(VirtualFileDrag.StartLocal(owner, new[] { empty }, token.Token) == DragDropEffects.None, "Cancelled drag entered OLE.");
                    Assert(VirtualFileDrag.StartLocal(owner, new string[0]) == DragDropEffects.None, "Empty drag entered OLE.");
                    Assert(!owner.IsHandleCreated, "The check must not create a window.");
                }
            });
            Check("Legacy deferred drag refuses without invoking the downloader", delegate
            {
                int downloads = 0; string status = null;
                using (var owner = new Control())
                {
                    var timer = Stopwatch.StartNew();
                    DragDropEffects result = VirtualFileDrag.Start(owner, new[] { Remote() },
                        delegate(PhoneFile file, CancellationToken token) { downloads++; return new TaskCompletionSource<string>().Task; },
                        CancellationToken.None, message => status = message);
                    Assert(result == DragDropEffects.None && downloads == 0, "Legacy path invoked deferred work.");
                    Assert(status != null && status.Contains("Fetch"), "Missing recovery instruction.");
                    Assert(timer.Elapsed < TimeSpan.FromSeconds(2), "Legacy refusal blocked.");
                    Assert(!owner.IsHandleCreated, "The check must not create a window.");
                }
            });
            Check("Large sparse file exports only a small path descriptor", delegate
            {
                string large = Path.Combine(fixtures, "large-sparse.bin"); owned.Add(large);
                using (var stream = new FileStream(large, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
                {
                    int returned;
                    Assert(DeviceIoControl(stream.SafeFileHandle, 0x000900c4, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero), "Sparse-file support unavailable.");
                    stream.SetLength(5L * 1024 * 1024 * 1024 + 7);
                }
                long before = GC.GetTotalMemory(true);
                AssertNativePaths(VirtualFileDrag.CreateLocalDataObject(new[] { empty, large }), new[] { empty, large });
                long growth = GC.GetTotalMemory(false) - before;
                Assert(growth < 8L * 1024 * 1024, "File contents were unexpectedly materialized.");
                Assert(new FileInfo(large).Length > UInt32.MaxValue, "The fixture did not exercise a large file.");
            });
            Check("Concurrent preparation is single-flight; waiter cancellation is isolated", delegate
            {
                int calls = 0;
                var producer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var service = new FileTransferService { Download = delegate { Interlocked.Increment(ref calls); return producer.Task; } };
                var cache = new StagedFiles(service, fixtures);
                using (var owner = new CancellationTokenSource())
                using (var waiter = new CancellationTokenSource())
                {
                    Task<string> first = cache.PrepareAsync("phone-a", Remote(), null, owner.Token);
                    Task<string> second = cache.PrepareAsync("phone-a", Remote(), null, waiter.Token);
                    waiter.Cancel();
                    Expect<OperationCanceledException>(delegate { Await(second); });
                    Assert(!first.IsCompleted && calls == 1, "A waiter cancelled/restarted the owner's download.");
                    producer.SetResult(empty);
                    Assert(Await(first) == empty, "The owned download did not complete.");
                    string path;
                    Assert(cache.TryGet("phone-a", Remote(), out path) && path == empty, "Completed copy was not cached.");
                    Assert(!cache.TryGet("phone-b", Remote(), out path), "A different phone reused the copy.");
                    PhoneFile changed = Remote(); changed.ModifiedUtc = changed.ModifiedUtc.AddSeconds(1);
                    Assert(!cache.TryGet("phone-a", changed, out path), "A newer remote version reused the old copy.");
                    cache.Clear();
                    Assert(File.Exists(empty), "Clearing the cache deleted a consumer's copy.");
                }
            });
            Check("Owner cancellation removes its flight and allows a clean retry", delegate
            {
                int calls = 0;
                var service = new FileTransferService { Download = delegate(string serial, PhoneFile file, CancellationToken token)
                {
                    if (Interlocked.Increment(ref calls) > 1) return Task.FromResult(empty);
                    var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    token.Register(delegate { pending.TrySetCanceled(); });
                    return pending.Task;
                } };
                var cache = new StagedFiles(service, fixtures);
                using (var owner = new CancellationTokenSource())
                {
                    Task<string> first = cache.PrepareAsync("phone-a", Remote(), null, owner.Token);
                    owner.Cancel(); Expect<OperationCanceledException>(delegate { Await(first); });
                }
                Assert(Await(cache.PrepareAsync("phone-a", Remote(), null, CancellationToken.None)) == empty && calls == 2,
                    "Cancelled preparation could not be retried.");
            });
            Check("Locally edited prepared copies are invalidated", delegate
            {
                var service = new FileTransferService { Download = delegate { return Task.FromResult(empty); } };
                var cache = new StagedFiles(service, fixtures);
                Await(cache.PrepareAsync("phone-a", Remote(), null, CancellationToken.None));
                File.SetLastWriteTimeUtc(empty, File.GetLastWriteTimeUtc(empty).AddSeconds(3));
                string path; Assert(!cache.TryGet("phone-a", Remote(), out path), "Edited copy stayed ready.");
            });
            Check("Windows export names retain extensions and reject reserved leaf names", delegate
            {
                Assert(VirtualFileDrag.ExportName("CON.txt") == "_CON.txt", "Reserved name escaped sanitization.");
                string name = VirtualFileDrag.ExportName(new String('x', 300) + ".pdf");
                Assert(name.Length <= 140 && name.EndsWith(".pdf", StringComparison.Ordinal), "Long filename lost its extension.");
            });
        }
        finally
        {
            foreach (string path in owned) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            try { Directory.Delete(fixtures, false); } catch (IOException) { }
            OleUninitialize();
        }
        Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed. No windows, Explorer sessions, network or phone were used.");
        return failed == 0 ? 0 : 1;
    }

    private static PhoneFile Remote()
    { return new PhoneFile { Name = "empty.txt", FullPath = "/sdcard/Download/empty.txt", Size = 0, ModifiedUtc = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc) }; }
    private static T Await<T>(Task<T> task)
    {
        if (Task.WhenAny(task, Task.Delay(5000)).GetAwaiter().GetResult() != task) throw new TimeoutException("Preparation did not complete within 5 seconds.");
        return task.GetAwaiter().GetResult();
    }
    private static void Check(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + " " + error.Message); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void AssertNativePaths(DataObject data, string[] expected)
    {
        var format = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        STGMEDIUM medium;
        ((System.Runtime.InteropServices.ComTypes.IDataObject)data).GetData(ref format, out medium);
        try
        {
            Assert(medium.tymed == TYMED.TYMED_HGLOBAL && medium.unionmember != IntPtr.Zero, "Invalid native DROPFILES medium.");
            Assert(GlobalSize(medium.unionmember).ToUInt64() < 65536, "Native drop unexpectedly contains file contents.");
            uint count = DragQueryFile(medium.unionmember, UInt32.MaxValue, null, 0);
            Assert(count == expected.Length, "Native DROPFILES count mismatch.");
            for (uint i = 0; i < count; i++)
            {
                uint length = DragQueryFile(medium.unionmember, i, null, 0);
                var text = new StringBuilder((int)length + 1);
                DragQueryFile(medium.unionmember, i, text, (uint)text.Capacity);
                Assert(text.ToString() == expected[i], "Native Unicode path mismatch.");
            }
        }
        finally { ReleaseStgMedium(ref medium); }
    }
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder name, uint count);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, int inputSize,
        IntPtr output, int outputSize, out int bytesReturned, IntPtr overlapped);
}
