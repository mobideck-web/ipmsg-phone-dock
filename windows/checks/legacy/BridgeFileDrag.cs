using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace IPMsg.Dock
{
    public enum BridgeDragOutcome { Completed, Cancelled, Failed, ReadyForRetry }
    public sealed class BridgeDragResult
    {
        public BridgeDragOutcome Outcome { get; set; }
        public string Message { get; set; }
        public Dictionary<string, string> ReadyFiles { get; set; }
    }
    public static class BridgeFileDrag
    {
        public static Task<BridgeDragResult> StartBrokerAsync(string executable, DockBridgeClient client,
            IList<BridgeItem> items, string privateCacheDirectory, CancellationToken token)
        {
            if (client == null) throw new ArgumentNullException("client");
            if (items == null || items.Count < 1 || items.Count > 30) throw new ArgumentException("Select up to 30 shared files.");
            List<BridgeItem> selected = items.Select(item => { DockBridgeClient.ValidateItem(item); return item.Snapshot(); }).ToList();
            if (selected.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != selected.Count) throw new ArgumentException("Select each shared file once.");
            string cache = Path.GetFullPath(privateCacheDirectory);
            if (cache.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Use a local private cache folder.");
            string application = Path.GetFullPath(executable);
            return Task.Run(async delegate
            {
                token.ThrowIfCancellationRequested();
                Process child = new Process();
                Task output = null, error = null;
                StringBuilder captured = new StringBuilder();
                object outputGate = new object();
                try
                {
                    child.StartInfo = new ProcessStartInfo(application, "--bridge-drag") { UseShellExecute = false,
                        CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
                    child.EnableRaisingEvents = true;
                    var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    child.Exited += delegate { exited.TrySetResult(true); };
                    if (!child.Start()) throw new IOException("Could not open the isolated file drag.");
                    output = CaptureAsync(child.StandardOutput, captured, outputGate);
                    error = CaptureAsync(child.StandardError, null, null);
                    if (child.HasExited) exited.TrySetResult(true);
                    using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        deadline.CancelAfter(TimeSpan.FromMinutes(20));
                        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        using (deadline.Token.Register(delegate { cancelled.TrySetResult(true); }))
                        {
                            string input = MakeInput(client, selected, cache);
                            // Framework StreamWriter may synchronously fill a
                            // redirected pipe before its first asynchronous yield.
                            Task sent = Task.Run(delegate { return SendInputAsync(child.StandardInput, input); });
                            Task sendObserved = sent.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                            // A helper that never consumes stdin can fill its pipe.
                            // Cancellation must cover this first write as well as OLE.
                            if (await Task.WhenAny(sent, exited.Task, cancelled.Task).ConfigureAwait(false) == sent)
                                await sent.ConfigureAwait(false);
                            if (await Task.WhenAny(exited.Task, cancelled.Task).ConfigureAwait(false) != exited.Task)
                            {
                                // A child still receiving its request cannot process
                                // a cancel command; kill it without a second pipe write.
                                if (sent.Status == TaskStatus.RanToCompletion)
                                {
                                    // Cancellation cannot synchronously write to a stuck child pipe on the UI thread.
                                    Task signal = Task.Run(delegate { try { child.StandardInput.WriteLine("cancel"); child.StandardInput.Flush(); } catch (Exception) { } });
                                    await Task.WhenAny(exited.Task, Task.Delay(2000)).ConfigureAwait(false);
                                }
                                if (!child.HasExited) AdbBridge.KillOwned(child);
                                return Result(BridgeDragOutcome.Cancelled, "File drag cancelled.");
                            }
                        }
                    }
                    await Task.WhenAny(Task.WhenAll(output, error), Task.Delay(1500)).ConfigureAwait(false);
                    string response; lock (outputGate) response = captured.ToString();
                    return ParseResult(response, cache, selected);
                }
                catch (Exception)
                {
                    AdbBridge.KillOwned(child);
                    return Result(token.IsCancellationRequested ? BridgeDragOutcome.Cancelled : BridgeDragOutcome.Failed,
                        "The Windows drag helper disconnected. Retry the drag or choose Save to PC.");
                }
                finally
                {
                    Task cleanup = CleanupAsync(child, output, error);
                    Task observed = cleanup.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }, token);
        }
        private static async Task SendInputAsync(StreamWriter input, string request)
        {
            await input.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(request))).ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
        }
        internal static BridgeDragResult Result(BridgeDragOutcome outcome, string message)
        {
            return new BridgeDragResult { Outcome = outcome, Message = message, ReadyFiles = new Dictionary<string, string>(StringComparer.Ordinal) };
        }
        private static async Task CaptureAsync(StreamReader reader, StringBuilder target, object gate)
        {
            char[] buffer = new char[2048];
            try
            {
                int count; while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
                    if (target != null) lock (gate) { if (target.Length + count > 65536) throw new IOException("Invalid drag response."); target.Append(buffer, 0, count); }
            }
            catch (IOException) { } catch (ObjectDisposedException) { }
        }
        private static async Task CleanupAsync(Process process, Task output, Task error)
        {
            try { if (output != null) await output.ConfigureAwait(false); if (error != null) await error.ConfigureAwait(false); }
            catch (Exception) { }
            finally { process.Dispose(); }
        }
        private static string MakeInput(DockBridgeClient client, IList<BridgeItem> items, string cache)
        {
            StringBuilder result = new StringBuilder();
            using (XmlWriter writer = XmlWriter.Create(result, new XmlWriterSettings { OmitXmlDeclaration = true }))
            {
                writer.WriteStartElement("drag"); writer.WriteAttributeString("port", client.Port.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("token", client.SessionToken); writer.WriteAttributeString("cache", cache);
                foreach (BridgeItem item in items)
                {
                    writer.WriteStartElement("file"); writer.WriteAttributeString("id", item.Id); writer.WriteAttributeString("name", item.Name);
                    writer.WriteAttributeString("mime", item.Mime); writer.WriteAttributeString("size", item.Size.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("modified", item.Modified.ToString(CultureInfo.InvariantCulture)); writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            return result.ToString();
        }
        internal static XmlDocument ParseXml(string input, int limit)
        {
            if (input == null || input.Length > limit) throw new IOException("Invalid drag message.");
            XmlDocument document = new XmlDocument(); document.XmlResolver = null;
            using (XmlReader reader = XmlReader.Create(new StringReader(input), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = limit, IgnoreWhitespace = true })) document.Load(reader);
            return document;
        }
        private static BridgeDragResult ParseResult(string xml, string cache, IList<BridgeItem> items)
        {
            XmlElement root = ParseXml(xml, 65536).DocumentElement;
            if (root == null || root.Name != "result") throw new IOException("The isolated file drag ended without a result.");
            BridgeDragOutcome outcome;
            if (!Enum.TryParse<BridgeDragOutcome>(root.GetAttribute("outcome"), out outcome)) throw new IOException("Invalid drag result.");
            BridgeDragResult result = Result(outcome, root.GetAttribute("message"));
            string prefix = cache.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (XmlNode node in root.ChildNodes)
            {
                XmlElement element = node as XmlElement; if (element == null || element.Name != "ready") throw new IOException("Invalid drag result.");
                string id = element.GetAttribute("id"); BridgeItem item = items.SingleOrDefault(i => i.Id == id);
                string path = Path.GetFullPath(element.GetAttribute("path"));
                if (item == null || result.ReadyFiles.ContainsKey(id) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(path) || new FileInfo(path).Length != item.Size) throw new IOException("Invalid prepared file result.");
                result.ReadyFiles.Add(id, path);
            }
            return result;
        }
    }
}
