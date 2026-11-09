using System;
using System.IO;
using System.Linq;
using System.Threading;

// Process fixture only. It never invokes ADB, connects to a phone, or opens a socket.
internal static class ConnectionFakeAdb
{
    private static string root;
    private static string Read(string name) { string path = Path.Combine(root, name); return File.Exists(path) ? File.ReadAllText(path) : ""; }
    public static int Main(string[] args)
    {
        root = Environment.GetEnvironmentVariable("IPMSG_CONNECTION_FIXTURE");
        if (String.IsNullOrEmpty(root)) return 2;
        string command = args.Length > 2 ? args[2] : "";
        File.AppendAllText(Path.Combine(root, "commands.log"), String.Join(" ", args.Select(a => a.Replace('\n', ' '))) + "\n");
        if (Read("hang-command") == command) { Thread.Sleep(20000); return 0; }
        if (command == "keygen") { File.WriteAllText(args[3], "fixture-key"); return 0; }
        if (command == "devices")
        {
            if (Read("listing-mode") == "truncated") { Console.Write(new String('x', 270000)); return 0; }
            Console.Write("List of devices attached\n" + Read("devices")); return 0;
        }
        if (command == "mdns") { Console.Write(Read("mdns")); return 0; }
        if (command == "disconnect") { Console.WriteLine("disconnected " + args[3]); return 0; }
        if (command == "connect")
        {
            string updated = Read("after-connect");
            if (updated.Length == 0) { Console.WriteLine("failed to connect"); return 1; }
            File.WriteAllText(Path.Combine(root, "devices"), updated);
            Console.WriteLine("connected to " + args[3]); return 0;
        }
        if (command == "-s" && args.Length > 5 && args[4] == "shell")
        {
            foreach (string line in Read("identities").Split('\n'))
            {
                string[] pair = line.TrimEnd('\r').Split('=');
                if (pair.Length != 2 || pair[0] != args[3]) continue;
                Console.WriteLine(pair[1]); Console.WriteLine(pair[1]); Console.WriteLine("fixture-android-id"); return 0;
            }
            Console.Error.WriteLine("device unavailable"); return 1;
        }
        return 1;
    }
}
