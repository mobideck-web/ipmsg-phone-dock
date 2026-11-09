using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IPMsg.Dock;

internal static class CrossProcessBridgeChecks
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--receive")
                return Task.Run(delegate { return Receive(args[1], args[2]); }).GetAwaiter().GetResult();
            return Source();
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static int Source()
    {
        string root = Path.Combine(Path.GetTempPath(), "ipmsg-cross-process-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int initialized = BridgeOle.OleInitialize(IntPtr.Zero);
        if (initialized < 0) Marshal.ThrowExceptionForHR(initialized);
        try
        {
            string original = Path.Combine(root, "original.txt"); File.WriteAllText(original, "hello", new UTF8Encoding(false));
            BridgeItem item = new BridgeItem { Id = new string('1', 32), Name = "original.txt", Mime = "text/plain", Size = 5, Modified = 1700000000000 };
            var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var data = new BridgeDataObject(new[] { item }, new Dictionary<string, Task<string>> { { item.Id, pending.Task } }, CancellationToken.None);
            data.StartOperation(null);
            FORMATETC format = BridgeDataObject.Format(BridgeDataObject.Contents, TYMED.TYMED_ISTREAM, 0);
            STGMEDIUM medium; data.GetData(ref format, out medium);
            try
            {
                string encoded = EncodeInterface(medium.unionmember, typeof(IStream).GUID), asyncEncoded;
                IntPtr capability = Marshal.GetComInterfaceForObject(data, typeof(IBridgeAsyncCapability));
                try { asyncEncoded = EncodeInterface(capability, typeof(IBridgeAsyncCapability).GUID); }
                finally { Marshal.Release(capability); }
                using (Process child = new Process())
                {
                    child.StartInfo = new ProcessStartInfo(typeof(CrossProcessBridgeChecks).Assembly.Location, "--receive " + encoded + " " + asyncEncoded) {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    child.Start();
                    Task release = Task.Run(async delegate { await Task.Delay(1000).ConfigureAwait(false); pending.SetResult(original); });
                    Stopwatch watch = Stopwatch.StartNew();
                    while (!child.HasExited && watch.ElapsedMilliseconds < 6000)
                    { uint index; BridgeOle.CoWaitForMultipleHandles(0x18, 50, 1, new[] { child.Handle }, out index); }
                    if (!child.HasExited) { child.Kill(); throw new Exception("Cross-process receiver exceeded 6 seconds."); }
                    Console.Write(child.StandardOutput.ReadToEnd()); Console.Error.Write(child.StandardError.ReadToEnd());
                    release.GetAwaiter().GetResult(); bool active; data.InOperation(out active);
                    Console.WriteLine("SOURCE Ended={0} InOperation={1} OperationFailed={2}", data.Ended.Task.IsCompleted, active, data.OperationFailed);
                    if (!data.Ended.Task.IsCompleted || active || data.OperationFailed) throw new Exception("Child EndOperation did not complete the source handshake.");
                    return child.ExitCode;
                }
            }
            finally { BridgeOle.ReleaseStgMedium(ref medium); }
        }
        finally
        {
            BridgeOle.OleUninitialize();
            string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("ipmsg-cross-process-probe-", StringComparison.Ordinal))
                try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
    private static string EncodeInterface(IntPtr pointer, Guid iid)
    {
        IStream storage; Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero, true, out storage));
        try
        {
            Marshal.ThrowExceptionForHR(CoMarshalInterface(storage, ref iid, pointer, 0, IntPtr.Zero, 0)); // MSHCTX_LOCAL, standard cross-process proxy.
            System.Runtime.InteropServices.ComTypes.STATSTG stat; storage.Stat(out stat, 1); byte[] bytes = new byte[checked((int)stat.cbSize)];
            storage.Seek(0, 0, IntPtr.Zero); storage.Read(bytes, bytes.Length, IntPtr.Zero);
            return Convert.ToBase64String(bytes);
        }
        finally { Marshal.ReleaseComObject(storage); }
    }
    private static IntPtr DecodeInterface(string encoded, Guid iid)
    {
        IStream storage; Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero, true, out storage));
        try
        {
            byte[] packet = Convert.FromBase64String(encoded); storage.Write(packet, packet.Length, IntPtr.Zero); storage.Seek(0, 0, IntPtr.Zero);
            IntPtr pointer; Marshal.ThrowExceptionForHR(CoUnmarshalInterface(storage, ref iid, out pointer)); return pointer;
        }
        finally { Marshal.ReleaseComObject(storage); }
    }
    private static T Method<T>(IntPtr pointer, int slot) where T : class
    { return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), slot * IntPtr.Size), typeof(T)); }
    private static int Receive(string encoded, string asyncEncoded)
    {
        int initialized = CoInitializeEx(IntPtr.Zero, 0);
        if (initialized < 0) Marshal.ThrowExceptionForHR(initialized);
        IntPtr proxy = IntPtr.Zero, capability = IntPtr.Zero, received = Marshal.AllocHGlobal(4);
        try
        {
            proxy = DecodeInterface(encoded, typeof(IStream).GUID);
            capability = DecodeInterface(asyncEncoded, typeof(IBridgeAsyncCapability).GUID);
            Marshal.WriteInt32(received, 0); int modeResult = Method<NativeBoolean>(capability, 4)(capability, received); bool mode = Marshal.ReadInt32(received) != 0;
            Marshal.WriteInt32(received, 0); int activeResult = Method<NativeBoolean>(capability, 6)(capability, received); bool active = Marshal.ReadInt32(received) != 0;
            int startResult = Method<NativeStart>(capability, 5)(capability, IntPtr.Zero);
            Console.WriteLine("ASYNC GetAsyncMode=0x{0:X8}/{1} InOperation=0x{2:X8}/{3} StartOperation=0x{4:X8}", modeResult, mode, activeResult, active, startResult);
            if (modeResult != 0 || !mode || activeResult != 0 || !active || startResult != 0) throw new Exception("Cross-process capability negotiation failed.");
            NativeRead read = Method<NativeRead>(proxy, 3);
            byte[] first = new byte[5]; Marshal.WriteInt32(received, 0); Stopwatch watch = Stopwatch.StartNew();
            int firstResult = read(proxy, first, first.Length, received);
            int firstCount = Marshal.ReadInt32(received);
            Console.WriteLine("FIRST hr=0x{0:X8} count={1} elapsedMs={2} bytes={3}", firstResult, firstCount, watch.ElapsedMilliseconds, Encoding.ASCII.GetString(first));
            Thread.Sleep(1600);
            byte[] second = new byte[5]; Marshal.WriteInt32(received, 0); watch.Restart();
            int secondResult = read(proxy, second, second.Length, received);
            Console.WriteLine("SECOND hr=0x{0:X8} count={1} elapsedMs={2}", secondResult, Marshal.ReadInt32(received), watch.ElapsedMilliseconds);
            bool contentOk = firstResult == 0 && firstCount == 5 && Encoding.ASCII.GetString(first) == "hello" && secondResult == 0 && Marshal.ReadInt32(received) == 0;
            int endResult = Method<NativeEnd>(capability, 7)(capability, contentOk ? 0 : unchecked((int)0x80004005), IntPtr.Zero, 1);
            Marshal.WriteInt32(received, 1); activeResult = Method<NativeBoolean>(capability, 6)(capability, received); active = Marshal.ReadInt32(received) != 0;
            Console.WriteLine("ASYNC EndOperation=0x{0:X8} InOperation=0x{1:X8}/{2}", endResult, activeResult, active);
            return contentOk && endResult == 0 && activeResult == 0 && !active ? 0 : 2;
        }
        finally
        {
            if (proxy != IntPtr.Zero) Marshal.Release(proxy);
            if (capability != IntPtr.Zero) Marshal.Release(capability);
            Marshal.FreeHGlobal(received); CoUninitialize();
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NativeRead(IntPtr stream, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer, int count, IntPtr received);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NativeBoolean(IntPtr instance, IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NativeStart(IntPtr instance, IntPtr reserved);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NativeEnd(IntPtr instance, int result, IntPtr reserved, uint effects);
    [DllImport("ole32.dll")] private static extern int CreateStreamOnHGlobal(IntPtr memory, [MarshalAs(UnmanagedType.Bool)] bool delete, out IStream stream);
    [DllImport("ole32.dll")] private static extern int CoMarshalInterface(IStream stream, ref Guid iid, IntPtr unknown, uint context, IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern int CoUnmarshalInterface(IStream stream, ref Guid iid, out IntPtr result);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
