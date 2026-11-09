using System.Threading;

// Deliberately unresponsive private child used only to verify that the parent's
// first redirected-pipe write is cancellable. It never opens a window or file.
internal static class BridgeBlockedInputFixture
{
    private static int Main()
    {
        Thread.Sleep(15000);
        return 0;
    }
}
