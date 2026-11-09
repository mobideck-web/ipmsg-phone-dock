# Validation — IPMsg Phone Dock 0.7.0 preview

Windows x64/.NET Framework 4.8 builds offline with its pinned scrcpy runtime, QRCoder, Lucide font and included notices. Main Windows executable compiles with the workspace compiler. The installer packages a verified portable payload for per-user installation. Windows binaries remain unsigned.

## Focused Windows checks

- 18 connection recovery and identity tests using generated fake ADB endpoints.
- 39 safe ZIP/folder materialization and native CF_HDROP checks, including ZIP64, traversal, reparse/symlink, duplicate names, cancellation, unique saves and exact cleanup.
- 38 authenticated loopback preparation tests, including actual-folder readiness, revoked selection, ordinary ZIP behavior and partial cleanup.
- 31 temporary-cache ownership and expiry checks.
- 10 fake-process/backend, 11 native data-object, 8 offscreen presentation, and 5 cancellation/lease checks.

Total: 160 focused local checks. These use generated local data and do not automate Explorer or claim physical-phone acceptance.

## Android source/build

Android remains application `app.lantern.lan`, minimum API 29 and target API 35. Version 0.8.1 increments code from 14 to 15. Its folder/protocol pure JVM fixtures cover ZIP bounds, empty/nested/Unicode content, unsafe names, changed providers, storage, cancellation, reserved-MIME handling and version-scoped gestures. The final Gradle release and full JVM test outcome will be filled from the completed build report. A final debug APK is never substituted for the owner-signed release APK.

## Acceptance still pending

Use the signed APK as an in-place upgrade; verify history is preserved, pick a folder on a real phone, allow PC access, wait for Ready and drop it into Explorer. Test empty/nested/Unicode/duplicate names, low storage, revoked selection, interruption, hotspot with mobile data off, phone lock and replacement USB/Wi-Fi addresses. Real provider behavior, real Explorer acceptance, Windows installer upgrade/uninstall, high DPI/multiple monitors and sustained performance require the owner’s devices.
