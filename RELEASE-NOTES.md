# IPMsg Phone Dock 0.7.0 preview

This release pairs the Windows Dock 0.7.0 preview with Android companion 0.8.1 RC1.

## Included

- Local Android phone control in a compact Windows dock over authorized USB or Wireless debugging.
- Two-way individual-file sharing and Android-selected folder sharing from phone to Windows using a completed, validated snapshot and native FileDrop.
- Recovery for stale USB/Wi-Fi ADB transports while retaining the selected phone identity.
- Android chat, local history, live camera and the existing IPMsg LAN features.

## Validation

- Windows: 160 focused local checks passed; Setup payload verification passed.
- Android: shrunk release build passed; 80 host JVM tests passed.
- Physical phone hotspot operation with mobile data disabled and an actual Explorer folder drop remain to be checked by the owner.

## Limits

Folder sharing completes a full snapshot, download and extraction before drag begins; large trees require time and temporary space. Folder transfers do not resume across process death. See the included review documents for security boundaries and remaining issues.

## Downloads

Use the installer for normal per-user setup or the portable archive for a no-install run. Install the signed Android APK over the existing app; do not uninstall first. Verify every asset with the attached SHA-256 file.
