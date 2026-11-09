using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IPMsg.Dock;

internal static class RegressionChecks
{
    private static string root;
    private static string state;
    private static string log;
    private static int passed;

    public static int Main()
    {
        try { RunAsync().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + passed + " focused backend regressions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task RunAsync()
    {
        root = AppDomain.CurrentDomain.BaseDirectory;
        state = Path.Combine(root, "state");
        log = Path.Combine(root, "fake-adb.log");
        Environment.SetEnvironmentVariable("IPMSG_FAKE_LOG", log);
        Environment.SetEnvironmentVariable("IPMSG_FAKE_MODE", "ok");
        AdbBridge bridge = new AdbBridge(Path.Combine(root, "runtime"), state);

        Stopwatch clock = Stopwatch.StartNew();
        CommandResult inherited = await bridge.RunAsync(new[] { "inherit" }, CancellationToken.None, 1000);
        Assert(clock.ElapsedMilliseconds < 2000 && inherited.Output == "parent-finished" && inherited.OutputTruncated,
            "inherited pipe returns a bounded, explicitly incomplete snapshot (" + clock.ElapsedMilliseconds + "ms)");

        string proxySessions = Path.Combine(state, "sessions"); Directory.CreateDirectory(proxySessions);
        Environment.SetEnvironmentVariable("IPMSG_DOCK_PROXY_RUNTIME", Path.Combine(root, "runtime"));
        Environment.SetEnvironmentVariable("IPMSG_DOCK_PROXY_STATE", state);
        Environment.SetEnvironmentVariable("IPMSG_DOCK_PROXY_SERIAL", "fake-phone");
        Environment.SetEnvironmentVariable("IPMSG_DOCK_PROXY_STATUS", Path.Combine(proxySessions, "fixture.status"));
        clock.Restart();
        int proxy = AdbProxy.Run(new[] { "exec-out", "proxy-inherit" });
        Assert(proxy == 0 && clock.ElapsedMilliseconds < 4000, "ADB proxy returns despite a descendant retaining its stderr pipe (" + clock.ElapsedMilliseconds + "ms)");

        CommandResult noisy = await bridge.RunAsync(new[] { "noisy" }, CancellationToken.None, 5000);
        Assert(noisy.ExitCode == 0 && noisy.Output.Length == 262144 && noisy.Error.Length == 262144 && noisy.OutputTruncated,
            "simultaneous stdout/stderr above pipe capacity drains without deadlock and remains bounded");

        clock.Restart();
        await ExpectAsync<TimeoutException>(bridge.RunAsync(new[] { "hang" }, CancellationToken.None, 350));
        Assert(clock.ElapsedMilliseconds < 2000, "hung adb obeys its deadline (" + clock.ElapsedMilliseconds + "ms)");

        using (CancellationTokenSource cancel = new CancellationTokenSource(250))
        {
            clock.Restart();
            await ExpectAsync<OperationCanceledException>(bridge.RunAsync(new[] { "hang" }, cancel.Token, 15000));
            Assert(clock.ElapsedMilliseconds < 2000, "hung adb cancellation finishes without blocking disposal (" + clock.ElapsedMilliseconds + "ms)");
        }

        string staging = Path.Combine(state, "regression-staging");
        FileTransferService transfers = new FileTransferService(bridge, staging, 5000);
        StagedFiles staged = new StagedFiles(transfers, state);
        File.WriteAllText(log, "");
        PhoneFile phone = Selected("one.bin");
        Task<string> owner = staged.PrepareAsync("fake-phone", phone, null, CancellationToken.None);
        using (CancellationTokenSource waiterCancel = new CancellationTokenSource(120))
        {
            Task<string> waiter = staged.PrepareAsync("fake-phone", phone, null, waiterCancel.Token);
            await ExpectAsync<OperationCanceledException>(waiter);
            string local = await owner;
            string ready;
            Assert(new FileInfo(local).Length == 4096 && staged.TryGet("fake-phone", phone, out ready) && ready == local && PullCount() == 1,
                "prepare is single-flight; cancelling a second waiter preserves the first download and ready copy");
        }

        Environment.SetEnvironmentVariable("IPMSG_FAKE_MODE", "hang");
        PhoneFile cancelledPhone = Selected("cancel.bin");
        using (CancellationTokenSource cancel = new CancellationTokenSource(250))
        {
            // A UI-like context that never pumps proves preparation does not depend on
            // the caller's message loop, and measures the synchronous entry-point cost.
            SynchronizationContext previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new UnpumpedContext());
            Task<string> preparation;
            long callTime;
            try
            {
                clock.Restart();
                preparation = staged.PrepareAsync("fake-phone", cancelledPhone, null, cancel.Token);
                callTime = clock.ElapsedMilliseconds;
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            await ExpectAsync<OperationCanceledException>(preparation);
            Assert(callTime < 250 && clock.ElapsedMilliseconds < 2000 && !Directory.GetFiles(staging, "*.part", SearchOption.AllDirectories).Any(),
                "prepare returns promptly on an unpumped UI context; cancellation removes its partial (entry " + callTime + "ms)");
        }

        Environment.SetEnvironmentVariable("IPMSG_FAKE_MODE", "ok");
        string retried = await staged.PrepareAsync("fake-phone", cancelledPhone, null, CancellationToken.None);
        Assert(new FileInfo(retried).Length == 4096, "cancelled preparation can be explicitly retried with the same selected file");

        Environment.SetEnvironmentVariable("IPMSG_FAKE_MODE", "hang");
        FileTransferService shortDeadline = new FileTransferService(bridge, staging, 350);
        clock.Restart();
        await ExpectAsync<TimeoutException>(shortDeadline.DownloadAsync("fake-phone", Selected("timeout.bin"), null, CancellationToken.None));
        Assert(clock.ElapsedMilliseconds < 2000 && !Directory.GetFiles(staging, "*.part", SearchOption.AllDirectories).Any(),
            "download deadline is enforced and its owned partial is cleaned");

        Environment.SetEnvironmentVariable("IPMSG_FAKE_MODE", "ok");
        File.WriteAllText(log, "");
        PhoneFile changed = Selected("changed.bin"); changed.Size = 12;
        await ExpectAsync<IOException>(transfers.DownloadAsync("fake-phone", changed, null, CancellationToken.None));
        Assert(PullCount() == 0, "changed selected file is rejected before a copy begins");
    }

    private static PhoneFile Selected(string name)
    {
        return new PhoneFile { Name = name, FullPath = "/sdcard/Download/" + name, Size = 4096,
            ModifiedUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(1700000000) };
    }
    private static int PullCount() { return File.ReadAllLines(log).Count(line => line.Contains(" pull ")); }
    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        passed++; Console.WriteLine("PASS " + passed + ": " + description);
    }
    private static async Task ExpectAsync<T>(Task task) where T : Exception
    {
        Task winner = await Task.WhenAny(task, Task.Delay(6000));
        if (winner != task) throw new Exception("Operation failed to settle within six seconds.");
        try { await task; } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class UnpumpedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object state) { }
    }
}
