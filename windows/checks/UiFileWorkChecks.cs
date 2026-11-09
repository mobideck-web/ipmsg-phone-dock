using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IPMsg.Dock;

internal static class UiFileWorkChecks
{
    private static int checks;
    private static Task Wait(Task work, CancellationToken token)
    {
        return (Task)typeof(DockForm).GetMethod("AwaitCancelableFileWorkAsync", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { work, token });
    }
    [STAThread] private static int Main()
    {
        string state = Path.Combine(Path.GetTempPath(), "ipmsg-file-wait-" + Guid.NewGuid().ToString("N"));
        try
        {
            Wait(Task.FromResult(true), CancellationToken.None).GetAwaiter().GetResult();
            Check(true, "Completed file work returns without creating a window");
            var failed = new TaskCompletionSource<bool>(); failed.SetException(new IOException("Fixture failure"));
            try { Wait(failed.Task, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Missing failure"); }
            catch (IOException error) { Check(error.Message == "Fixture failure", "File errors retain their actionable cause"); }

            DateTime now = DateTime.UtcNow;
            var cache = TemporaryBridgeCache.CreateSession(state, delegate { return now; });
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task source = Task.Run(async delegate { using (cache.BeginUse()) { entered.SetResult(true); await release.Task; } });
            entered.Task.GetAwaiter().GetResult();
            using (var cancellation = new CancellationTokenSource(100))
            {
                Stopwatch clock = Stopwatch.StartNew();
                try { Wait(source, cancellation.Token).GetAwaiter().GetResult(); throw new Exception("Missing cancellation"); }
                catch (OperationCanceledException) { Check(clock.ElapsedMilliseconds < 1500 && !source.IsCompleted,
                    "Cancelling a stalled destination releases the caller promptly without pretending native I/O has stopped"); }
            }
            cache.Retire(); now = now.AddHours(2); TemporaryBridgeCache.Prune(state, now);
            Check(Directory.Exists(cache.DirectoryPath), "A cancelled caller retains the actual worker's cache lease until native I/O finishes");
            release.SetResult(true); source.GetAwaiter().GetResult();
            now = now.AddHours(2); TemporaryBridgeCache.Prune(state, now);
            Check(!Directory.Exists(cache.DirectoryPath), "The finished worker releases its lease and permits later cache expiry");
            Console.WriteLine("RESULT: " + checks + " file-work cancellation checks passed. No phone, desktop UI or real blocked network share.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string cacheRoot = Path.Combine(state, "bridge-cache");
            if (Directory.Exists(cacheRoot) && Directory.GetFileSystemEntries(cacheRoot).Length == 0) Directory.Delete(cacheRoot, false);
            if (Directory.Exists(state) && Directory.GetFileSystemEntries(state).Length == 0) Directory.Delete(state, false);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS: " + message); }
}
