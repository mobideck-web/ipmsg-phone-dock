using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IPMsg.Dock;

internal static class BridgeRegression
{
    private const string Id = "11111111111111111111111111111111";
    private static readonly string Token = new String('a', 64); // Public fixture, never a real session.
    private static int passed;
    private static string root;
    [STAThread]
    public static int Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "ipmsg-bridge-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int initialized = BridgeOle.OleInitialize(IntPtr.Zero);
        try
        {
            if (initialized < 0) Marshal.ThrowExceptionForHR(initialized);
            HttpChecks().GetAwaiter().GetResult(); ComChecks(); ReadyLocalDropChecks();
            if (args.Length == 2) StartupChecks(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
            else throw new ArgumentException("Pass the compiled IPMsgDock WinExe and blocked-input fixture to exercise broker startup.");
            Console.WriteLine("RESULT: " + passed + " bridge checks passed. Real WinExe startup, loopback fixtures and COM media only; no phone, window, mouse drag or Explorer used.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            if (initialized >= 0) BridgeOle.OleUninitialize();
            // This exact new fixture directory owns every file below it.
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
    private static void StartupChecks(string executable, string blockedBroker)
    {
        Check(File.Exists(executable), "Compiled GUI executable is available for real subprocess checks");
        using (Fixture server = new Fixture())
        {
            string cache = Path.Combine(root, "broker-文件-é");
            string request = "<drag port=\"" + server.Port + "\" token=\"" + Token + "\" cache=\"" + cache +
                "\"><file id=\"" + Id + "\" name=\"文件-é-😀.txt\" mime=\"text/plain\" size=\"5\" modified=\"1700000000000\" /></drag>";
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(request));
            // Never start OLE while a real user drag is in progress. No mouse input
            // is synthesized; the valid request must take its released-button path.
            if ((BridgeOle.GetAsyncKeyState(1) & 0x8000) != 0) throw new Exception("Release the left mouse button before running broker startup checks.");
            Stopwatch clock = Stopwatch.StartNew();
            BrokerReply reply = RunBroker(executable, encoded, true);
            Check(reply.ExitCode == 0 && reply.Outcome == "Cancelled" && reply.Message.Contains("holding") &&
                clock.ElapsedMilliseconds < 5000 && server.Requests == 0 && !Directory.Exists(cache),
                "Real WinExe accepts UTF-8 request/cache paths over redirected stdin and returns XML without downloading after mouse release (" + clock.ElapsedMilliseconds + "ms)");
            for (int i = 0; i < 4; i++)
            {
                reply = RunBroker(executable, encoded, false);
                Check(reply.ExitCode == 0 && reply.Outcome == "Cancelled", "Repeated GUI broker startup " + (i + 1) + " completes with stdin held open");
            }
            reply = RunBroker(executable, "not-base64!", true);
            Check(reply.ExitCode == 1 && reply.Outcome == "Failed", "Malformed request returns a bounded failure XML instead of closing the parent's write pipe");
            reply = RunBroker(executable, Convert.ToBase64String(new byte[] { 0xc3, 0x28 }), true);
            Check(reply.ExitCode == 1 && reply.Outcome == "Failed", "Invalid UTF-8 is rejected by the actual GUI broker");
            reply = RunBroker(executable, null, true);
            Check(reply.ExitCode == 1 && reply.Outcome == "Failed" && server.Requests == 0, "Parent EOF before request exits promptly without networking");
            using (CancellationTokenSource cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Expect<OperationCanceledException>(delegate { BridgeFileDrag.StartBrokerAsync(executable,
                    new DockBridgeClient(server.Port, Token), new[] { Item() }, cache, cancelled.Token).GetAwaiter().GetResult(); });
                Pass("Pre-cancelled parent request never starts a drag or download");
            }
            var many = Enumerable.Range(0, 30).Select(i => new BridgeItem { Id = i.ToString("x32"),
                Name = new String('x', 500) + ".txt", Mime = "text/plain", Size = 5, Modified = 1700000000000 }).ToList();
            using (CancellationTokenSource cancelled = new CancellationTokenSource(150))
            {
                clock.Restart();
                Task<BridgeDragResult> blocked = BridgeFileDrag.StartBrokerAsync(blockedBroker,
                    new DockBridgeClient(server.Port, Token), many, cache, cancelled.Token);
                if (!blocked.Wait(4000)) throw new Exception("Blocked initial broker write ignored cancellation.");
                Check(blocked.Result.Outcome == BridgeDragOutcome.Cancelled && clock.ElapsedMilliseconds < 3500,
                    "Parent cancellation terminates a helper that never reads its full initial stdin pipe (" + clock.ElapsedMilliseconds + "ms)");
            }
        }
    }
    private sealed class BrokerReply { public int ExitCode; public string Outcome, Message; }
    private static BrokerReply RunBroker(string executable, string request, bool closeInput)
    {
        using (Process process = new Process())
        {
            process.StartInfo = new ProcessStartInfo(executable, "--bridge-drag") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true) };
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            try
            {
                if (request != null) { process.StandardInput.WriteLine(request); process.StandardInput.Flush(); }
                if (closeInput) process.StandardInput.Close();
                if (!process.WaitForExit(5000)) throw new Exception("GUI broker did not exit within five seconds.");
                if (!Task.WaitAll(new Task[] { output, error }, 1500)) throw new Exception("GUI broker left an inherited pipe open.");
                string xml = output.Result;
                if (xml.Length > 65536 || error.Result.Length != 0 || xml.IndexOf(Token, StringComparison.Ordinal) >= 0)
                    throw new Exception("GUI broker emitted invalid or sensitive protocol output.");
                System.Xml.XmlElement result = BridgeFileDrag.ParseXml(xml, 65536).DocumentElement;
                if (result == null || result.Name != "result") throw new Exception("GUI broker did not emit a result XML.");
                return new BrokerReply { ExitCode = process.ExitCode, Outcome = result.GetAttribute("outcome"), Message = result.GetAttribute("message") };
            }
            finally { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }
        }
    }
    private static async Task HttpChecks()
    {
        Expect<ArgumentException>(delegate { new DockBridgeClient(1234, "bad"); });
        Expect<ArgumentOutOfRangeException>(delegate { new DockBridgeClient(0, Token); });
        Pass("Invalid bearer/port rejected before networking");
        using (Fixture server = new Fixture())
        {
            DockBridgeClient client = new DockBridgeClient(server.Port, Token);
            server.Reply = request => Response.Json(Status());
            BridgeStatus status = await client.GetStatusAsync(CancellationToken.None);
            Check(status.Items.Count == 1 && status.Items[0].Id == Id && server.LastAuthorization == "Bearer " + Token,
                "Strict status schema parses and every request carries explicit bearer authentication");
            foreach (string malformed in new[] { Status().Replace("\"version\":1", "\"version\":1,\"version\":1"),
                Status().Replace("\"size\":5", "\"size\":-1"), Status().Replace(Id, "../unsafe"),
                Status().Replace("\"version\":1", "\"version\":1.0"), Status() + "false" })
            {
                server.Reply = request => Response.Json(malformed);
                await Reject<IOException>(client.GetStatusAsync(CancellationToken.None));
            }
            Pass("Duplicate keys, negative sizes, path-like IDs, floating version and trailing JSON rejected");
            server.Reply = request => Response.Json(Status().Replace("[" + ItemJson() + "]", "[" + String.Join(",", Enumerable.Range(0, 31).Select(i => ItemJson().Replace(Id, i.ToString("x32")))) + "]"));
            await Reject<IOException>(client.GetStatusAsync(CancellationToken.None));
            Pass("Oversized item list rejected");
            using (Fixture trap = new Fixture())
            {
                server.Reply = request => new Response { Status = "302 Found", Extra = "Location: http://127.0.0.1:" + trap.Port + "/stolen\r\n" };
                await Reject<IOException>(client.GetStatusAsync(CancellationToken.None));
                await Task.Delay(80);
                Check(trap.Requests == 0, "Redirect is rejected without forwarding bearer credentials");
            }
            server.Reply = request => new Response { Body = new byte[262145] };
            await Reject<IOException>(client.GetThumbnailAsync(Id, CancellationToken.None));
            Pass("Oversized thumbnail rejected before decoding");
            server.Reply = request => new Response { Body = Jpeg(2048, 2048) };
            await Reject<IOException>(client.GetThumbnailAsync(Id, CancellationToken.None));
            server.Reply = request => new Response { Body = new byte[] { 255, 216, 255, 255, 255, 192 } };
            await Reject<IOException>(client.GetThumbnailAsync(Id, CancellationToken.None));
            Pass("Huge dimensions and truncated JPEG markers rejected safely");
            server.Reply = request => new Response { Body = Jpeg(320, 180) };
            Check((await client.GetThumbnailAsync(Id, CancellationToken.None)).Length > 0, "Bounded thumbnail JPEG header accepted");
            BridgeItem item = Item();
            server.Reply = request => new Response { Body = Encoding.ASCII.GetBytes("hello") };
            string completed = await client.DownloadAsync(item, root, null, CancellationToken.None);
            Check(File.ReadAllText(completed) == "hello" && !completed.EndsWith(".part"), "Only a verified complete file is published");
            server.Reply = request => new Response { Body = new byte[0] };
            BridgeItem empty = Item(); empty.Size = 0;
            Check(new FileInfo(await client.DownloadAsync(empty, root, null, CancellationToken.None)).Length == 0, "Empty shared file completes correctly");
            server.Reply = request => new Response { Body = Encoding.ASCII.GetBytes("hello"), Length = 6 };
            await Reject<IOException>(client.DownloadAsync(item, root, null, CancellationToken.None));
            server.Reply = request => new Response { Body = Encoding.ASCII.GetBytes("hel"), Length = 5 };
            await Reject<IOException>(client.DownloadAsync(item, root, null, CancellationToken.None));
            Check(!Directory.GetFiles(root, "*.part", SearchOption.AllDirectories).Any(), "Wrong Content-Length and premature EOF never leave a published file or partial");
            server.Reply = request => new Response { Body = new byte[] { 1 }, Length = 5, Delay = 10000 };
            using (CancellationTokenSource cancel = new CancellationTokenSource(150))
            {
                Stopwatch clock = Stopwatch.StartNew();
                await Reject<OperationCanceledException>(client.DownloadAsync(item, root, null, cancel.Token));
                Check(clock.ElapsedMilliseconds < 2500 && !Directory.GetFiles(root, "*.part", SearchOption.AllDirectories).Any(),
                    "Cancellation aborts a stalled socket promptly and removes its private partial");
            }
            int requests = server.Requests;
            server.Reply = request => new Response { Body = Encoding.ASCII.GetBytes("hello") };
            BridgeLeaseStore first = new BridgeLeaseStore(client, new[] { item }, root, CancellationToken.None);
            await first.All;
            string path = first.Tasks[Id].Result;
            BridgeLeaseStore second = new BridgeLeaseStore(client, new[] { item }, root, CancellationToken.None);
            await second.All;
            Check(second.Tasks[Id].Result == path && server.Requests == requests + 1, "Same-session immutable item/version reuses the completed lease without another transfer");
            File.AppendAllText(path, "changed");
            BridgeLeaseStore third = new BridgeLeaseStore(client, new[] { item }, root, CancellationToken.None);
            await third.All;
            Check(third.Tasks[Id].Result != path && File.ReadAllText(third.Tasks[Id].Result) == "hello", "Locally changed cached bytes invalidate readiness and fetch a new private copy");
        }
        Expect<System.Xml.XmlException>(delegate { BridgeFileDrag.ParseXml("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><x>&e;</x>", 1024); });
        Pass("Broker XML rejects DTD/external entities");
    }
    private static void ComChecks()
    {
        BridgeItem item = Item();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var copies = new Dictionary<string, Task<string>> { { Id, pending.Task } };
        var data = new BridgeDataObject(new[] { item }, copies, CancellationToken.None);
        Guid capability = new Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4");
        IntPtr unknown = Marshal.GetIUnknownForObject(data), asyncPointer;
        try { Check(Marshal.QueryInterface(unknown, ref capability, out asyncPointer) == 0, "COM QueryInterface exposes the actual async-drop capability GUID"); Marshal.Release(asyncPointer); }
        finally { Marshal.Release(unknown); }
        FORMATETC descriptorFormat = BridgeDataObject.Format(BridgeDataObject.Descriptor, TYMED.TYMED_HGLOBAL, -1);
        STGMEDIUM descriptor; Stopwatch clock = Stopwatch.StartNew(); data.GetData(ref descriptorFormat, out descriptor);
        IntPtr memory = BridgeOle.GlobalLock(descriptor.unionmember);
        try
        {
            BridgeDescriptor entry = (BridgeDescriptor)Marshal.PtrToStructure(IntPtr.Add(memory, 4), typeof(BridgeDescriptor));
            Check(clock.ElapsedMilliseconds < 250 && Marshal.ReadInt32(memory) == 1 && entry.Name == item.Name && entry.SizeLow == 5,
                "Native FILEGROUPDESCRIPTOR is immediate and contains metadata without waiting for bytes");
        }
        finally { BridgeOle.GlobalUnlock(descriptor.unionmember); BridgeOle.ReleaseStgMedium(ref descriptor); }
        FORMATETC streamFormat = BridgeDataObject.Format(BridgeDataObject.Contents, TYMED.TYMED_ISTREAM, 0);
        clock.Restart();
        Expect<COMException>(delegate { STGMEDIUM refused; data.GetData(ref streamFormat, out refused); });
        Check(clock.ElapsedMilliseconds < 250 && data.NeedsReadyRetry, "Synchronous target is refused immediately while originals are still transferring");
        var asyncData = new BridgeDataObject(new[] { item }, copies, CancellationToken.None);
        Check(asyncData.StartOperation(null) == 0, "Target can explicitly begin an asynchronous operation");
        STGMEDIUM medium; clock.Restart(); asyncData.GetData(ref streamFormat, out medium);
        try
        {
            IStream stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
            Check(clock.ElapsedMilliseconds < 250, "Async GetData returns an IStream immediately, without executing a download or wait");
            clock.Restart(); Expect<COMException>(delegate { stream.Read(new byte[1], 1, IntPtr.Zero); });
            Check(clock.ElapsedMilliseconds < 250, "Pending stream never waits on the source COM apartment");
            byte[] read = new byte[5];
            Task worker = Task.Run(delegate { stream.Read(read, 5, IntPtr.Zero); });
            Thread.Sleep(50);
            clock.Restart(); IStream clone; stream.Clone(out clone); stream.Seek(0, 0, IntPtr.Zero);
            Check(clock.ElapsedMilliseconds < 250, "A pending worker does not hold the position lock needed by source seek/clone");
            string complete = Path.Combine(root, "stream-complete.txt"); File.WriteAllText(complete, "hello", new UTF8Encoding(false)); pending.SetResult(complete);
            Check(worker.Wait(2500) && Encoding.ASCII.GetString(read) == "hello", "Negotiated worker reads the completed original after asynchronous preparation");
            clone.Seek(1, 0, IntPtr.Zero); byte[] tail = new byte[4]; clone.Read(tail, 4, IntPtr.Zero);
            System.Runtime.InteropServices.ComTypes.STATSTG stat; clone.Stat(out stat, 1);
            Check(Encoding.ASCII.GetString(tail) == "ello" && stat.cbSize == 5, "Cloned stream has independent position and exact known size");
            asyncData.EndOperation(0, null, 1);
            Check(asyncData.Ended.Task.IsCompleted && !asyncData.OperationFailed, "Async completion is tracked until receiver EndOperation");
        }
        finally { BridgeOle.ReleaseStgMedium(ref medium); }
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
            Check(BridgeDragBroker.CompleteDrop(data, pending.Task, false, cancellation).Outcome == BridgeDragOutcome.ReadyForRetry,
                "Refused synchronous target still offers ready retry when no drop occurred");
        MarshaledStreamCheck(item, streamFormat);
        FailureCompletionChecks();
    }
    private static void FailureCompletionChecks()
    {
        BridgeItem first = Item(), second = Item(); second.Id = "22222222222222222222222222222222";
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var data = new BridgeDataObject(new[] { first }, new Dictionary<string, Task<string>> { { Id, pending.Task } }, CancellationToken.None);
        data.StartOperation(null);
        IntPtr capability = Marshal.GetComInterfaceForObject(data, typeof(IBridgeAsyncCapability));
        try
        {
            IntPtr function = Marshal.ReadIntPtr(Marshal.ReadIntPtr(capability), 7 * IntPtr.Size);
            var end = (NativeEndOperation)Marshal.GetDelegateForFunctionPointer(function, typeof(NativeEndOperation));
            Check(end(capability, unchecked((int)0x80004004), IntPtr.Zero, 0) == 0 && data.OperationFailed,
                "Native receiver EndOperation reports a failed extraction while source bytes remain pending");
            using (CancellationTokenSource cancellation = new CancellationTokenSource(2000))
            using (cancellation.Token.Register(delegate { pending.TrySetCanceled(); }))
            {
                Stopwatch clock = Stopwatch.StartNew();
                Expect<IOException>(delegate { BridgeDragBroker.CompleteDrop(data, pending.Task, true, cancellation); });
                Check(cancellation.IsCancellationRequested && clock.ElapsedMilliseconds < 1500,
                    "Receiver failure cancels preparation promptly instead of waiting for stalled source bytes");
            }
        }
        finally { Marshal.Release(capability); }
        for (int cancelledSource = 0; cancelledSource < 2; cancelledSource++)
        {
            var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stalled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            data = new BridgeDataObject(new[] { first, second }, new Dictionary<string, Task<string>> {
                { first.Id, failed.Task }, { second.Id, stalled.Task } }, CancellationToken.None);
            data.StartOperation(null);
            Task all = Task.WhenAll(failed.Task, stalled.Task);
            using (CancellationTokenSource cancellation = new CancellationTokenSource(2000))
            using (cancellation.Token.Register(delegate { stalled.TrySetCanceled(); }))
            {
                bool cancel = cancelledSource != 0;
                Task trigger = Task.Run(async delegate { await Task.Delay(50).ConfigureAwait(false);
                    if (cancel) failed.TrySetCanceled(); else failed.TrySetException(new IOException("Fixture source disconnected.")); });
                Stopwatch clock = Stopwatch.StartNew();
                if (cancel) Expect<OperationCanceledException>(delegate { BridgeDragBroker.CompleteDrop(data, all, true, cancellation); });
                else Expect<IOException>(delegate { BridgeDragBroker.CompleteDrop(data, all, true, cancellation); });
                Check(!data.Ended.Task.IsCompleted && cancellation.IsCancellationRequested && clock.ElapsedMilliseconds < 1500,
                    (cancel ? "Cancelled" : "Failed") + " source interrupts missing receiver EndOperation even while another source is stalled");
            }
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NativeEndOperation(IntPtr capability, int result, IntPtr reserved, uint effects);
    private static void ReadyLocalDropChecks()
    {
        string directory = Path.Combine(root, "ready-文件-é-😀"); Directory.CreateDirectory(directory);
        string original = Path.Combine(directory, "文件-é-😀.txt"), empty = Path.Combine(directory, "empty-零.bin");
        File.WriteAllText(original, "hello", new UTF8Encoding(false)); File.WriteAllBytes(empty, new byte[0]);
        BridgeItem first = Item(), second = new BridgeItem { Id = "22222222222222222222222222222222", Name = Path.GetFileName(empty), Mime = "application/octet-stream", Size = 0, Modified = first.Modified };
        var copies = new Dictionary<string, Task<string>> { { first.Id, Task.FromResult(original) }, { second.Id, Task.FromResult(empty) } };
        var data = new BridgeDataObject(new[] { first, second }, copies, CancellationToken.None);
        FORMATETC drop = BridgeDataObject.Format(BridgeDataObject.FileDrop, TYMED.TYMED_HGLOBAL, -1);
        FORMATETC[] offered = new FORMATETC[4]; int[] fetched = new int[1];
        Check(data.EnumFormatEtc(DATADIR.DATADIR_GET).Next(4, offered, fetched) == 0 && fetched[0] == 4 &&
            offered[0].cfFormat == BridgeDataObject.FileDrop && data.QueryGetData(ref drop) == 0,
            "A fully prepared selection offers standard CF_HDROP first while preserving virtual file formats");
        STGMEDIUM medium; data.GetData(ref drop, out medium);
        try
        {
            string[] paths = new string[2]; uint count = DragQueryFile(medium.unionmember, UInt32.MaxValue, null, 0);
            for (uint i = 0; i < paths.Length; i++)
            {
                uint length = DragQueryFile(medium.unionmember, i, null, 0); StringBuilder path = new StringBuilder((int)length + 1);
                DragQueryFile(medium.unionmember, i, path, (uint)path.Capacity); paths[i] = path.ToString();
            }
            Check(medium.tymed == TYMED.TYMED_HGLOBAL && count == 2 && paths[0] == original && paths[1] == empty && new FileInfo(paths[1]).Length == 0,
                "Native Shell CF_HDROP decoding preserves Unicode paths and includes a ready zero-byte original");
        }
        finally { BridgeOle.ReleaseStgMedium(ref medium); }
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var incomplete = new BridgeDataObject(new[] { first, second }, new Dictionary<string, Task<string>> { { first.Id, copies[first.Id] }, { second.Id, pending.Task } }, CancellationToken.None);
        offered = new FORMATETC[4]; fetched[0] = 0;
        Check(incomplete.EnumFormatEtc(DATADIR.DATADIR_GET).Next(4, offered, fetched) == 1 && fetched[0] == 3 &&
            offered.Take(fetched[0]).All(format => format.cfFormat != BridgeDataObject.FileDrop) && incomplete.QueryGetData(ref drop) != 0,
            "A selection with any pending original never advertises or accepts CF_HDROP");
        Expect<COMException>(delegate { STGMEDIUM refused; incomplete.GetData(ref drop, out refused); });
        pending.SetResult(empty);
        Check(incomplete.QueryGetData(ref drop) != 0,
            "Completing downloads during a drag does not change its initial CF_HDROP format snapshot");
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder path, uint count);
    private static void MarshaledStreamCheck(BridgeItem item, FORMATETC format)
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var data = new BridgeDataObject(new[] { item }, new Dictionary<string, Task<string>> { { Id, pending.Task } }, CancellationToken.None);
        Expect<COMException>(delegate { STGMEDIUM refused; data.GetData(ref format, out refused); });
        Check(data.NeedsReadyRetry, "Target's synchronous pending-file preflight sets the retry flag before async negotiation");
        data.StartOperation(null);
        STGMEDIUM medium; data.GetData(ref format, out medium);
        Guid streamId = new Guid("0000000C-0000-0000-C000-000000000046"); IntPtr marshaled;
        try
        {
            Marshal.ThrowExceptionForHR(CoMarshalInterThreadInterfaceInStream(ref streamId, medium.unionmember, out marshaled));
            string path = Path.Combine(root, "marshaled-complete.txt"); File.WriteAllText(path, "hello", new UTF8Encoding(false));
            string destination = Path.Combine(root, "receiver-destination.txt");
            Task release = Task.Run(async delegate { await Task.Delay(200).ConfigureAwait(false); pending.TrySetResult(path); });
            Task<string> target = Task.Run(delegate
            {
                int initialized = CoInitializeEx(IntPtr.Zero, 0);
                if (initialized < 0) Marshal.ThrowExceptionForHR(initialized);
                IntPtr proxy = IntPtr.Zero, received = Marshal.AllocHGlobal(4);
                try
                {
                    Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(marshaled, ref streamId, out proxy));
                    // Invoke the native IStream vtable rather than letting an RCW
                    // unwrap the CCW back to the original managed object. This
                    // exercises Windows COM apartment marshaling like a target.
                    IntPtr readFunction = Marshal.ReadIntPtr(Marshal.ReadIntPtr(proxy), 3 * IntPtr.Size);
                    var read = (NativeRead)Marshal.GetDelegateForFunctionPointer(readFunction, typeof(NativeRead));
                    byte[] bytes = new byte[5]; Marshal.WriteInt32(received, 0);
                    int result = read(proxy, bytes, bytes.Length, received);
                    if (result < 0) throw new COMException("Marshaled target Read failed.", result);
                    if (Marshal.ReadInt32(received) != 5) throw new Exception("Marshaled target did not read all bytes.");
                    // The original is ready before the receiving application has
                    // finished its destination write. Keep its COM server alive
                    // through EndOperation despite the earlier preflight retry.
                    Thread.Sleep(200);
                    File.WriteAllBytes(destination, bytes);
                    data.EndOperation(0, null, 1);
                    return Encoding.ASCII.GetString(bytes);
                }
                finally { if (proxy != IntPtr.Zero) Marshal.Release(proxy); Marshal.FreeHGlobal(received); CoUninitialize(); }
            });
            using (CancellationTokenSource cancellation = new CancellationTokenSource(3000))
            {
                BridgeDragResult result = BridgeDragBroker.CompleteDrop(data, pending.Task, true, cancellation);
                Check(result.Outcome == BridgeDragOutcome.Completed && data.Ended.Task.IsCompleted && File.Exists(destination) &&
                    File.ReadAllText(destination) == "hello", "Successful async drop with prior preflight retry waits for receiver EndOperation and its destination write");
            }
            using (WaitHandle ready = ((IAsyncResult)target).AsyncWaitHandle)
            {
                Stopwatch clock = Stopwatch.StartNew();
                while (!target.IsCompleted && clock.ElapsedMilliseconds < 3000)
                { uint index; BridgeOle.CoWaitForMultipleHandles(0x18, 50, 1, new[] { ready.SafeWaitHandle.DangerousGetHandle() }, out index); }
            }
            Check(target.IsCompleted && target.GetAwaiter().GetResult() == "hello", "Native COM-marshaled async target reads after delayed preparation while broker pumps its source apartment");
        }
        finally { BridgeOle.ReleaseStgMedium(ref medium); }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NativeRead(IntPtr stream, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, int count, IntPtr received);
    [DllImport("ole32.dll")] private static extern int CoMarshalInterThreadInterfaceInStream(ref Guid iid, IntPtr unknown, out IntPtr stream);
    [DllImport("ole32.dll")] private static extern int CoGetInterfaceAndReleaseStream(IntPtr stream, ref Guid iid, out IntPtr result);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    private static BridgeItem Item() { return new BridgeItem { Id = Id, Name = "sample.txt", Mime = "text/plain", Size = 5, Modified = 1700000000000 }; }
    private static string ItemJson() { return "{\"id\":\"" + Id + "\",\"name\":\"sample.txt\",\"mime\":\"text/plain\",\"size\":5,\"modified\":1700000000000,\"thumbnail\":false}"; }
    private static string Status() { return "{\"version\":1,\"expiresAt\":1900000000000,\"revision\":1,\"items\":[" + ItemJson() + "],\"gesture\":{\"id\":\"\",\"revision\":0,\"phase\":\"end\",\"itemIds\":[]}}"; }
    private static byte[] Jpeg(int width, int height) { return new byte[] { 255, 216, 255, 192, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 17, 0, 2, 17, 0, 3, 17, 0, 255, 217 }; }
    private static void Pass(string message) { passed++; Console.WriteLine("PASS " + passed + ": " + message); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Pass(message); }
    private static void Expect<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task Reject<T>(Task task) where T : Exception
    { if (await Task.WhenAny(task, Task.Delay(5000)) != task) throw new Exception("Operation did not finish within five seconds."); try { await task; } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    private sealed class Response
    {
        public string Status = "200 OK", Extra = ""; public byte[] Body = new byte[0]; public long Length = -1; public int Delay;
        public static Response Json(string text) { return new Response { Body = Encoding.UTF8.GetBytes(text) }; }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly TcpListener listener; public int Port; public Func<string, Response> Reply = request => Response.Json("{}");
        public int Requests; public string LastAuthorization;
        public Fixture()
        {
            listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task loop = Accept();
        }
        private async Task Accept()
        {
            while (true)
            {
                TcpClient client; try { client = await listener.AcceptTcpClientAsync(); } catch (Exception) { return; }
                Task work = Task.Run(delegate { Handle(client); });
            }
        }
        private void Handle(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = 3000; StringBuilder header = new StringBuilder();
                    while (header.Length < 8192 && !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    { int next = stream.ReadByte(); if (next < 0) return; header.Append((char)next); }
                    Interlocked.Increment(ref Requests);
                    string auth = header.ToString().Split('\n').FirstOrDefault(h => h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
                    LastAuthorization = auth == null ? null : auth.Substring(auth.IndexOf(':') + 1).Trim();
                    Response response = Reply(header.ToString());
                    byte[] bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + response.Status + "\r\nContent-Length: " + (response.Length < 0 ? response.Body.Length : response.Length) + "\r\nConnection: close\r\n" + response.Extra + "\r\n");
                    stream.Write(bytes, 0, bytes.Length); stream.Write(response.Body, 0, response.Body.Length); stream.Flush();
                    if (response.Delay > 0) Thread.Sleep(response.Delay);
                }
            }
            catch (Exception) { }
        }
        public void Dispose() { listener.Stop(); }
    }
}
