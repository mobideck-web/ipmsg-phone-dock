# Folder sharing

1. Connect the phone and click **Files** in Phone Dock.
2. Approve the temporary PC session on the phone.
3. Tap **Folder**, choose a folder in Android’s system picker, and wait for its snapshot to finish.
4. In Windows, wait for the card to say **Ready to drag**, then drag it into Explorer. To save it explicitly, press Enter or right-click and choose Save.

Folder contents stay within the selected tree. Empty folders, nested files, and Unicode names are preserved. The app sends a bounded snapshot, then Windows reconstructs a normal folder.

Limits: 10,000 descendant entries; depth 32; at most 32 GiB for both archive and expanded contents; 10-minute phone snapshot deadline; 32 MiB phone free-space reserve. Windows checks archive structure before extraction and won’t overwrite an existing destination. It rejects path traversal, links, reserved/invalid names, case-only collisions and paths beyond a conservative 240-character limit. Select a smaller folder or rename offending items when a check fails.

Preparing a complete folder before Windows can drag it takes time and temporary space, particularly for large folders. The phone checks known-size folder capacity before reading, and checks unknown provider sizes while copying. If Windows is killed during Save, a hidden `.ipmsg-folder-<id>.part` may remain inside the chosen destination. Check and remove that partial folder manually. It is not marked Saved unless complete.
