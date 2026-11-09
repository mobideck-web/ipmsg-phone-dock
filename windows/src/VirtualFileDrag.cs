using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IPMsg.Dock
{
    /// <summary>
    /// Exports completed local copies with the standard Windows FileDrop format.
    /// No phone/network work, custom COM callbacks, or nested message pumps run
    /// while Windows owns the mouse drag.
    /// </summary>
    public static class VirtualFileDrag
    {
        public static DragDropEffects StartLocal(Control owner, IList<string> paths)
        {
            return StartLocal(owner, paths, CancellationToken.None);
        }

        public static DragDropEffects StartLocal(Control owner, IList<string> paths,
            CancellationToken cancellationToken)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (paths == null) throw new ArgumentNullException("paths");
            if (paths.Count == 0 || cancellationToken.IsCancellationRequested)
                return DragDropEffects.None;
            if (owner.IsDisposed || owner.InvokeRequired ||
                Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Start the file drag from the dock's UI thread.");
            if ((Control.MouseButtons & (MouseButtons.Left | MouseButtons.Right)) == MouseButtons.None)
                return DragDropEffects.None;

            DataObject data = CreateLocalDataObject(paths);
            var elapsed = Stopwatch.StartNew();
            QueryContinueDragEventHandler cancel = delegate(object sender, QueryContinueDragEventArgs args)
            {
                // End a forgotten mouse hold at the next OLE source callback,
                // as well as honouring Esc. No download waits in this loop.
                if (args.EscapePressed || cancellationToken.IsCancellationRequested ||
                    elapsed.Elapsed > TimeSpan.FromMinutes(2)) args.Action = DragAction.Cancel;
            };
            owner.QueryContinueDrag += cancel;
            try { return owner.DoDragDrop(data, DragDropEffects.Copy); }
            finally { owner.QueryContinueDrag -= cancel; }
        }

        internal static DataObject CreateLocalDataObject(IList<string> paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (paths.Count == 0) throw new ArgumentException("Fetch a phone file to PC before dragging.");
            var local = new StringCollection();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (String.IsNullOrWhiteSpace(path)) throw new IOException("Fetch the selected files to PC again.");
                string full = Path.GetFullPath(path);
                // Prepared files are local snapshots, never UNC/network paths:
                // even checking a disconnected share here could block the UI.
                if (full.StartsWith(@"\\", StringComparison.Ordinal))
                    throw new IOException("Drag a fetched local copy, rather than a network path.");
                if (!File.Exists(full) && !Directory.Exists(full))
                    throw new FileNotFoundException("A fetched file is no longer available. Fetch it again.", full);
                // A directory is a normal CF_HDROP source too. Never hand the
                // shell a reparse root or a path beneath a junction.
                string current = full;
                while (!String.IsNullOrEmpty(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Fetch the selected local folder again; its path is a link.");
                    string parent = Path.GetDirectoryName(current); if (parent == current) break; current = parent;
                }
                if (used.Add(full)) local.Add(full);
            }
            var data = new DataObject();
            data.SetFileDropList(local);
            return data;
        }

        /// <summary>
        /// Compatibility guard for old call sites. Downloading within OLE
        /// GetData can block and re-enter the UI; that path has been removed.
        /// Fetch asynchronously before starting a new local file drag.
        /// </summary>
        public static DragDropEffects Start(Control owner, IList<PhoneFile> files,
            Func<PhoneFile, CancellationToken, Task<string>> stage,
            CancellationToken cancellationToken, Action<string> status)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (files == null) throw new ArgumentNullException("files");
            if (files.Count == 0 || cancellationToken.IsCancellationRequested) return DragDropEffects.None;
            if (status != null) status("Fetch files to PC, then drag their ready copies.");
            return DragDropEffects.None;
        }

        /// <summary>Shared Windows leaf-name policy for downloaded copies.</summary>
        public static string ExportName(string original)
        {
            var text = new StringBuilder();
            foreach (char c in original ?? "File")
                text.Append(Char.IsControl(c) || "<>:\"/\\|?*".IndexOf(c) >= 0 ? '_' : c);
            string name = text.ToString().Trim().TrimEnd('.');
            if (name.Length == 0 || name == "." || name == "..") name = "File";
            string stem = name.Split('.')[0].TrimEnd(' ', '.');
            string upper = stem.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL" ||
                upper == "CONIN$" || upper == "CONOUT$" ||
                (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) ||
                upper.StartsWith("LPT", StringComparison.Ordinal)) &&
                ((upper[3] >= '1' && upper[3] <= '9') || "¹²³".IndexOf(upper[3]) >= 0)))
                name = "_" + name;
            return FitName(name, 140);
        }

        /// <summary>Keep native Shell FileDrop source paths within a conservative
        /// 240-character budget, even when session staging directories are nested.</summary>
        public static string ExportNameForDirectory(string original, string directory)
        {
            string full;
            try { full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (PathTooLongException) { throw CachePathTooLong(); }
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Use a local private cache folder.");
            int budget = Math.Min(140, 240 - full.Length - 1);
            if (budget < 16) throw CachePathTooLong();
            return FitName(ExportName(original), budget);
        }

        internal static IOException CachePathTooLong()
        { return new IOException("The temporary cache path is too long. Use a shorter local data folder and retry."); }

        private static string FitName(string name, int length)
        {
            // Keep a useful extension and never split a UTF-16 surrogate pair.
            if (name.Length > length)
            {
                string extension = Path.GetExtension(name);
                if (extension.Length > Math.Min(20, length - 1)) extension = Limit(extension, Math.Min(20, length - 1));
                string main = Path.GetFileNameWithoutExtension(name);
                name = Limit(main, length - extension.Length).TrimEnd(' ', '.') + extension;
            }
            return name.TrimEnd(' ', '.');
        }

        private static string Limit(string text, int length)
        {
            if (text.Length <= length) return text;
            if (length > 0 && Char.IsHighSurrogate(text[length - 1])) length--;
            return text.Substring(0, Math.Max(0, length));
        }

    }
}
