# IPMsg Dock Windows Setup

`build-setup.ps1` produces one offline, per-user Windows x64 installer using the installed .NET Framework C# compiler. Setup does not request administrator privileges or download dependencies.

From the repository workspace:

```powershell
& ./outputs/IPMsgDock/build-setup.ps1 `
    -PayloadZip ./outputs/IPMsg-Phone-Dock-v0.6.0-preview-win64.zip `
    -Version 0.6.0-preview `
    -OutputPath ./outputs/IPMsg-Phone-Dock-v0.6.0-preview-Setup.exe
```

The ZIP can contain application files at its root or under the single known `IPMsg-Phone-Dock/` prefix. Every original file, including bundled runtime, license files, and corresponding source archives, is retained. The build adds a small standalone `Uninstall.exe`, creates a SHA-256/size manifest for all installed files, and embeds the normalized ZIP and manifest into Setup. A separate `.sha256.txt` accompanies the installer. Intermediate files remain in a unique `work/dock-setup-build-*` directory.

Installation creates `%LOCALAPPDATA%\Programs\IPMsgDock`, a Start menu shortcut, and an Apps/Add or Remove Programs entry under HKCU. The Desktop shortcut is optional. Start at sign-in is opt-in and launches `IPMsgDock.exe --background`; its checkbox starts off. The app owns the configurable keyboard shortcut, so installer shortcuts have no separate `.lnk` hotkey. Setup can open the app after Finish.

Updates and uninstall preserve `%LOCALAPPDATA%\IPMsgDock`, which contains private settings, pairing material, and history. Setup verifies every payload hash before installing, checks existing ownership and file locks, and asks the user to exit the running app and phone sessions before retrying. Updates reject modified or conflicting files and roll back file changes if installation fails. A per-user named mutex prevents concurrent install/uninstall operations.

Uninstall deletes only receipt-tracked files whose size and hash still match, and only installer-created shortcuts whose bytes still match. Changed or unrelated files remain. Empty directories are removed individually. Startup and uninstall registry values are removed only when they belong to this installation. No broad recursive deletion or shell deletion command is used. The uninstaller runs a small copy from the normal Windows temporary directory so that it can remove its installed executable; Windows may retain that temporary helper until ordinary temporary-file cleanup.

Run the isolated regression checks:

```powershell
& ./outputs/IPMsgDock/setup/run-checks.ps1
```

These checks compile a console harness and use a new workspace fixture directory. They cover unsafe paths, a real NTFS junction, ZIP links, duplicate entries, file hashes, missing/unlisted payload files, fresh install, upgrade, lock failure, rollback, modified-file preservation, untracked-file preservation, and a private-state sentinel. They do not install into the user's Programs directory, create actual Start menu/Desktop shortcuts, write HKCU, or launch the phone app.

Verify an installer without installing it:

```powershell
$report = [IO.Path]::GetFullPath('./work/setup-verify.txt') # Must be a new file.
$process = Start-Process ./outputs/IPMsg-Phone-Dock-v0.6.0-preview-Setup.exe `
    -ArgumentList @('--verify', ('"' + $report + '"')) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw 'Embedded payload verification failed.' }
Get-Content -LiteralPath $report
```

This checks each embedded file against its size and SHA-256 manifest and creates only the requested report. It does not establish publisher authenticity; release signing, when available, is a separate packaging step.
