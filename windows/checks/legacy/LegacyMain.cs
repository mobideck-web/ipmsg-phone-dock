using System;
namespace IPMsg.Dock {
    internal static class LegacyMain {
        [STAThread] private static int Main(string[] args) {
            return args.Length == 1 && args[0] == "--bridge-drag" ? BridgeDragBroker.Run() : 1;
        }
    }
}
