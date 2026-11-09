using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

internal static class FakeAdb
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "holder") { Thread.Sleep(12000); return 0; }
        string command = args.Length > 2 ? args[2] : "";
        string log = Environment.GetEnvironmentVariable("IPMSG_FAKE_LOG");
        if (!String.IsNullOrEmpty(log)) File.AppendAllText(log, String.Join(" ", args.Select(a => a.Replace('\n', ' '))) + "\n");
        if (command == "inherit" || args.Contains("proxy-inherit"))
        {
            ProcessStartInfo start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "holder");
            start.UseShellExecute = false; start.CreateNoWindow = true;
            Process.Start(start);
            Console.Write("parent-finished");
            return 0;
        }
        if (command == "hang") { Thread.Sleep(20000); return 0; }
        if (command == "noisy")
        {
            for (int n = 0; n < 160; n++) { Console.Write(new String('a', 4096)); Console.Error.Write(new String('b', 4096)); }
            return 0;
        }
        if (command == "keygen") { File.WriteAllText(args[3], "fake-key"); return 0; }
        int offset = Array.IndexOf(args, "shell");
        if (offset >= 0)
        {
            string script = args[offset + 1];
            if (script.Contains("stat -c '%s %Y'")) { Console.WriteLine("4096 1700000000"); return 0; }
            return 0;
        }
        offset = Array.IndexOf(args, "pull");
        if (offset >= 0)
        {
            string mode = Environment.GetEnvironmentVariable("IPMSG_FAKE_MODE");
            if (mode == "hang") { Thread.Sleep(20000); return 0; }
            string destination = args[offset + 2];
            using (FileStream file = File.Create(destination))
                for (int n = 0; n < 16; n++) { file.Write(new byte[256], 0, 256); file.Flush(); Thread.Sleep(60); }
            return 0;
        }
        return 0;
    }
}
