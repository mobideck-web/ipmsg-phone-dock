# IPMsg Phone Dock 0.7.0 preview

A compact, local-only Windows companion for IPMsg on Android. Windows x64, .NET Framework 4.8; Android 10+. No account, cloud relay, internet connection or added runtime service. The application ID and installed phone data remain intact.

## Connect

1. On the old dock, choose **Exit IPMsg Dock** in the tray menu. Closing its window hides it.
2. Run `IPMsg-Phone-Dock-v0.7.0-preview-Setup.exe`, or extract the complete portable ZIP and run `IPMsgDock.exe` with the included runtime and licenses.
3. Connect your own phone by USB and approve its Android debugging prompt. The dock confirms the phone before granting first-time access. Use More → Connection → Set up Wi-Fi for Android 11+ Wireless debugging. Pair once, then use Connect.

The earlier “phone offline” warning could occur when the remembered USB/Wi-Fi address was stale. The dock now checks other transports against the remembered phone identity and retries only that phone’s saved Wi-Fi endpoint. It keeps the requested phone selected if the old transport disappears. It will not silently pick another phone. Recovery is bounded; check USB, the pairing, and Wireless debugging if the phone stays unavailable. Offline hotspot use is supported when the local network permits peer-to-peer communication; mobile data is not required.

## Share files and folders

Click **Files**, approve the temporary session on the phone, then choose Files, Photos, or Folder in the Android picker. Folder selects one tree with Android’s system folder picker. The phone prepares one bounded private archive; Windows checks it and materializes it as a real folder. The Windows card shows the folder’s original name and a folder icon. Wait until **Ready to drag**, then drag the card into Explorer or another Windows app that accepts ordinary file drops. Use Enter or the context menu to save a copy to a chosen PC folder. Name collisions get a suffix; existing files and folders are not overwritten.

Folder snapshots preserve empty and nested folders, subject to the limits in `FOLDER-SHARING.md`. Unsafe or case-conflicting Windows names, virtual documents, changed or unreadable selections, and folders too large for available storage fail clearly. Very deep paths can exceed Windows’ safe Shell path limit. A complete local copy must be prepared before dragging, so large folders take time. The tray is temporary; Clear ends the session and revokes the phone selection.

Individual files and photos use the same tray. Windows receives a normal native FileDrop from the dock thread after preparation; the drag callback performs no phone or network work. PC-to-phone drops send individual files into Downloads/IPMsg. This update does not add PC-to-phone folder transfers.

## Recovery and privacy

The phone session only exposes items explicitly selected or shared to IPMsg. It ends when stopped, expired, the phone locks, or the PC closes the connection. There are no broad storage permissions or persisted folder grants. Ready local sources are kept in an app-owned cache for a one-hour grace period after retirement or the last active copy, then pruned every five minutes while the dock runs and on the next launch. This is ordinary deletion, not secure erasure. Windows destination files are not part of cache cleanup.

A process killed during **Save folder** may leave a hidden `.ipmsg-folder-<id>.part` staging folder in the selected destination. It is never treated as a completed save and is deliberately not deleted later by a broad destination sweep. If this rare interruption occurs, remove that clearly named partial folder manually after checking its contents.

## Limits and checks

Setup and portable Windows builds are unsigned previews. Their payload hashes are internally checked; they are not publisher signatures. The Android 0.8.1 release is built with shrinking. Install only the APK signed with IPMsg’s existing key and lineage. A fresh unsigned debug/release build cannot update the installed app while preserving its identity.

The focused local suites cover connection identity, bounded folder snapshots/extraction, native CF_HDROP data, preparation, cache ownership, and cancellation. Actual phone folder picking, owner grants, and a mouse drop into Explorer still need acceptance on the phone and Windows session. See `REVIEW.md`, `IMPLEMENTATION.md`, and `GUI-CHECKS.md`.
