# IPMsg Android folder sharing

Use Windows Phone Dock 0.7.0 with IPMsg Android 0.8.1 or newer. Connect, open Files and approve the temporary owner session. Tap **Folder**, choose one tree using Android’s system folder picker, and wait for a bounded local archive snapshot. Windows downloads, verifies and extracts it as a real folder before showing **Ready to drag**. No general storage permission, persistent URI grant, or raw phone path is exposed.

The archive uses ZIP without recompression for speed. Empty/nested folders and Unicode names are retained. It is limited to 10,000 descendants, depth 32, 32 GiB, a 10-minute preparation deadline and a 32 MiB storage reserve. Known-size content is checked against available space before opening. Unknown-size providers are bounded while streaming. It rejects virtual items, repeated IDs/cycles, Windows-invalid or case-conflicting names, and source changes detected while snapshotting; nothing is silently skipped.

The phone must have temporary space for the archive, and Windows needs enough space to hold and extract it. Large folders take time because a complete snapshot must exist before drag begins. Only versions that understand the reserved folder format display a Windows folder; older dock versions see the archive as a file. PC-to-phone folders are not part of this update.
