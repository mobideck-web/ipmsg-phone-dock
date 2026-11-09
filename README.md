# IPMsg Phone Dock

A local-first Windows dock for using an Android phone from the PC. Connect over USB or Wi-Fi debugging, control the phone in a compact dock, exchange files, drag selected phone files and folders into Windows, and use the companion's chat and live-camera tools. LAN features work without an account, relay, or mobile data.

This is an independent project and is not affiliated with the original IP Messenger maintainers.

## Current release

- **Windows Dock:** `0.7.0-preview` — Windows x64, per-user Setup and portable build.
- **Android companion:** `0.8.1 RC1` — Android 10+ (`app.lantern.lan`).

Download the signed Android APK, Windows installer, portable package, source archives, setup guide, and checksum file from the GitHub **Releases** page. Verify SHA-256 values against the release checksum file. The APK preserves the installed app identity; install it over the existing app and do not uninstall first.

## Quick start

1. Install the Windows Dock and the signed Android companion from the latest release.
2. Connect the phone and PC to the same Wi-Fi, or connect the PC to the phone's hotspot. **Mobile data and internet are not required.** The hotspot must allow its connected devices to communicate locally.
3. Connect the Android phone by USB and approve this PC's Android debugging key, or pair Wireless debugging in the Dock's Connection menu.
4. Open the phone app, start listening, then select the same phone and click Connect in Windows.
5. To send a folder to Windows, open **Files** in the Dock, approve the temporary phone session, choose **Folder** in Android's system picker, and wait for **Ready to drag**. Drag the completed folder card into Explorer.

The Dock remembers a phone identity across USB/Wi-Fi transport changes. It checks only remembered endpoints and does not silently switch to a different connected phone. If it reports offline, unlock the phone and check USB debugging or Wireless debugging, refresh the device list, and reconnect.

## Folder transfer behavior

Folders are snapshotted on the phone, downloaded and validated on Windows, then exposed as ordinary Windows folders. The full snapshot must be ready before the drag starts, so very large folders take time and temporary storage. The implementation bounds count, depth, size and duration, rejects unsafe Windows paths, and does not overwrite existing files. See [folder sharing](windows/FOLDER-SHARING.md) and [Android folder protocol](android/DOCK-FOLDERS.md).

## Source layout

- `windows/` — Windows Forms dock, ADB/scrcpy integration, setup packaging, notices and focused checks.
- `android/` — Android app and Dock companion protocol, tests and build wrapper.
- Each component contains its own README, license, third-party notices and review notes.

## Build from source

Windows builds require Windows, .NET Framework 4.8 developer tools and PowerShell. From the repository root:

```powershell
.\windows\build.ps1
```

The Android project uses JDK 17 and Android SDK 35. From the repository root:

```powershell
.\android\gradlew.bat -p .\android :app:assembleRelease :app:testDebugUnitTest
```

Release signing keys and lineage are intentionally excluded. The unsigned build cannot update an installed release; use the verified release APK or sign with the owner's existing release lineage.

## Validation and remaining limits

The current candidate passed 160 focused Windows checks and 80 Android host JVM tests. These do not substitute for physical-device acceptance. Hotspot behavior with mobile data disabled and an actual folder drop into Explorer still need testing on the user's devices. Folder transfers prepare a complete snapshot before drag and do not resume across process death. See [Windows review](windows/REVIEW.md), [security notes](windows/SECURITY-REVIEW.md), and [Android review](android/REVIEW.md).

## License

The Windows and Android application components are MIT-licensed; see [LICENSE](LICENSE), [windows/LICENSE](windows/LICENSE) and [android/LICENSE](android/LICENSE). Third-party components retain their own terms and notices in the component directories.
