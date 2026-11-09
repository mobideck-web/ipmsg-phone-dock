using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IPMsg.Dock
{
    public static class PhoneScreenshot
    {
        public const int MaximumEncodedBytes = 64 * 1024 * 1024;
        private const long MaximumPixels = 32L * 1024 * 1024;

        // Reads the phone's own screencap PNG over the explicitly selected authorized ADB
        // transport. This never captures the Windows desktop or uses a screen-scraping API.
        public static Task<Bitmap> CaptureAsync(AdbBridge bridge, string serial, CancellationToken token)
        {
            if (bridge == null) throw new ArgumentNullException("bridge");
            AdbBridge.ValidateSerial(serial);
            return Task.Run(async delegate
            {
                await bridge.EnsureReadyAsync(token).ConfigureAwait(false);
                byte[] png = await CapturePngAsync(bridge, serial, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return DecodePng(png);
            }, token);
        }

        public static Bitmap DecodePng(byte[] png)
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (png == null || png.Length < 33 || png.Length > MaximumEncodedBytes)
                throw new IOException("The phone returned an incomplete or oversized screenshot.");
            for (int index = 0; index < signature.Length; index++)
                if (png[index] != signature[index]) throw new IOException("The phone did not return a PNG screenshot.");
            if (Read32(png, 8) != 13 || png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R')
                throw new IOException("The phone returned an invalid screenshot header.");
            int end = png.Length - 12;
            if (Read32(png, end) != 0 || png[end + 4] != 'I' || png[end + 5] != 'E' || png[end + 6] != 'N' || png[end + 7] != 'D' || Read32(png, end + 8) != 0xAE426082)
                throw new IOException("The phone screenshot did not finish downloading.");
            uint width = Read32(png, 16), height = Read32(png, 20);
            if (width == 0 || height == 0 || width > 16384 || height > 16384 || (long)width * height > MaximumPixels)
                throw new IOException("The screenshot dimensions exceed the safe image limit.");
            try
            {
                using (MemoryStream stream = new MemoryStream(png, false))
                using (Image image = Image.FromStream(stream, false, true))
                {
                    if (image.Width != width || image.Height != height) throw new IOException("Screenshot dimensions do not match its header.");
                    Bitmap bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
                    try { using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.DrawImageUnscaled(image, 0, 0); }
                    catch { bitmap.Dispose(); throw; }
                    return bitmap;
                }
            }
            catch (ArgumentException error) { throw new IOException("The phone returned a damaged PNG screenshot.", error); }
        }

        private static uint Read32(byte[] bytes, int offset)
        { return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3]; }

        private static async Task<byte[]> CapturePngAsync(AdbBridge bridge, string serial, CancellationToken token)
        {
            string arguments = "-P " + AdbBridge.ServerPort + " -s " + AdbBridge.QuoteArgument(serial) + " exec-out screencap -p";
            ProcessStartInfo start = new ProcessStartInfo(bridge.AdbPath, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(bridge.AdbPath),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            bridge.ConfigureEnvironment(start);
            Process process = new Process { StartInfo = start, EnableRaisingEvents = true };
            Task<byte[]> bytes = null; Task<string> errors = null;
            bool started = false;
            try
            {
                token.ThrowIfCancellationRequested();
                TaskCompletionSource<bool> exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += delegate { exited.TrySetResult(true); };
                if (!process.Start()) throw new IOException("Could not start the phone screenshot command.");
                started = true;
                bytes = ReadPngAsync(process.StandardOutput.BaseStream, delegate { AdbBridge.KillOwned(process); });
                errors = ReadErrorsAsync(process.StandardError);
                if (process.HasExited) exited.TrySetResult(true);
                using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(15000);
                    TaskCompletionSource<bool> interrupted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (deadline.Token.Register(delegate { interrupted.TrySetResult(true); }))
                    {
                        Task complete = Task.WhenAll(bytes, errors, exited.Task);
                        if (await Task.WhenAny(complete, interrupted.Task).ConfigureAwait(false) != complete)
                        {
                            AdbBridge.KillOwned(process);
                            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                            throw new TimeoutException("The phone screenshot timed out. Check the connection and unlock the phone if Android requires it.");
                        }
                        await complete.ConfigureAwait(false);
                        if (process.ExitCode != 0)
                            throw new IOException("Phone screenshot failed: " + (await errors.ConfigureAwait(false)).Trim());
                        token.ThrowIfCancellationRequested();
                        return await bytes.ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (!started) process.Dispose();
                else
                {
                    // Framework pipe Dispose can block on an outstanding read. Keep cleanup off
                    // the caller and let pending reads finish, just like the main ADB bridge.
                    Task cleanup = CleanupAsync(process, bytes, errors);
                    Observe(cleanup);
                }
            }
        }

        private static async Task<byte[]> ReadPngAsync(Stream source, Action fail)
        {
            try
            {
                using (MemoryStream target = new MemoryStream())
                {
                    byte[] buffer = new byte[32768];
                    while (true)
                    {
                        int count = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                        if (count == 0) return target.ToArray();
                        if (target.Length + count > MaximumEncodedBytes) throw new IOException("The phone screenshot exceeded 64 MiB.");
                        target.Write(buffer, 0, count);
                    }
                }
            }
            catch { fail(); throw; }
        }

        private static async Task<string> ReadErrorsAsync(StreamReader reader)
        {
            StringBuilder text = new StringBuilder(); char[] buffer = new char[1024];
            try
            {
                while (true)
                {
                    int count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (count == 0) break;
                    text.Append(buffer, 0, count); if (text.Length > 4096) text.Remove(0, text.Length - 4096);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            return text.ToString();
        }

        private static async Task CleanupAsync(Process process, Task bytes, Task errors)
        {
            try { await Task.WhenAll(bytes ?? Task.FromResult(false), errors ?? Task.FromResult(false)).ConfigureAwait(false); }
            catch (Exception) { }
            finally { process.Dispose(); }
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        // Call and await from the WinForms UI thread. Retry only a temporarily busy clipboard.
        public static async Task SetClipboardAsync(Image image, CancellationToken token)
        {
            if (image == null) throw new ArgumentNullException("image");
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA || SynchronizationContext.Current == null)
                throw new InvalidOperationException("Copy the screenshot from the dock's UI thread.");
            for (int attempt = 0; attempt < 5; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try { Clipboard.SetImage(image); return; }
                catch (ExternalException)
                {
                    if (attempt == 4) throw new IOException("The Windows clipboard is busy. Try Copy screenshot again.");
                }
                await Task.Delay(90, token);
            }
        }
    }
}
