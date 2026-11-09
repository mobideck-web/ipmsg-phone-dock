# IPMsg 0.4.0 validation

Date: November 5, 2026. Development test APK; Android 10+.

## Build

- App name IPMsg, package app.lantern.lan, version code 4 / version name 0.4.0.
- Android SDK 35, JDK 17, Kotlin 2.1.20, AGP 8.9.1, Gradle 8.11.1.
- APK signature verified; certificate matches the previous Lantern versions for in-place upgrade.
- 27 unit tests pass: 13 protocol/cryptography, 7 network safety, 5 file completion/cancellation/streaming regressions and 2 packet-rate budget tests.
- Lint passes with zero errors; advisory dependency/platform/style warnings remain.
- File transport uses reusable 128 KiB buffers and a larger TCP receive buffer. These reduce copying/allocations; real Wi-Fi speed has not been benchmarked.

## Android runtime

Final APK on Android 15: all eight engine/database cases passed in 20.316 seconds, and all four UI cases passed in 62.078 seconds. The app installed over v0.3.0 successfully. UI checks include source-chat saved-photo cards with Open/Folder controls, photo/video attachment actions, and notification switches visibly toggling and persisting both ways. The engine/database cases verify actual UDP/TCP sockets, signed encryption, retries, duplicate suppression, transfer completion/truncation/cancellation, migration, replay persistence, strict plaintext rejection, device removal/restoration, saved-file reopening and cancelled camera targets.

The final visual smoke check passes all four tabs and actual foreground-service start/stop. The Home-screen widget was pinned on the Android 15 launcher and its Open/Connect/Stop actions were exercised; its label changed between Online and Offline. Android's notification manager reports the alerts-v4 channel at importance 4 with vibration enabled, and in-app connection banners were visually observed.

The final camera handoff was exercised through Android's actual camera app using an emulated camera (no host webcam). Photo capture returned to IPMsg's explicit sharing confirmation. Video capture recorded an MP4 (689,957 bytes / 21.649 seconds) and also returned to that confirmation. Both test shares were cancelled; no photo/video offer was sent to Windows. This verifies capture-to-confirmation on the emulator, not physical-phone camera/audio quality. The emulator was launched with host audio disabled.

Visual checks found and corrected indefinite action banners and stale switch presentation under Compose skipping. Banners now expire, include a dismiss action and replace older banners; widget state refreshes when the app resumes. Each preference observes state directly so its switch reflects the saved value immediately. The final APK passes all 27 unit tests and lint with zero errors again.

New runtime cases cover database v3 to v4 migration, durable source-chat file links and expired session offers, cancelled photo/video MediaStore targets, persistent device blocking/restoring, strict message rejection over real UDP, source-chat file cards, Open/Folder controls and attachment camera/video actions. The previous engine, migration, replay, cancellation, encryption, draft and search checks remain included.

## Scope and limitations

- Camera capture is delegated to an installed Android camera application. It provides the preview and photo/video recording controls. No remote live stream, remote storage browsing, camera/microphone permission or broad all-files permission is added.
- Successful captures are saved to Pictures/IPMsg or Movies/IPMsg and are shared only after the user confirms the offer. Targets are ordinary local media rows so the external camera can write them; they may be visible in local media apps during capture. Cancelled/failed launches delete their MediaStore target rows. Forced termination can leave an empty or partial local media file requiring manual cleanup.
- Settings storage browsing is local. Chat attachment selection is a separate explicit sharing action. Folder actions depend on provider support and can fall back to the system file browser. Saved URI access depends on the provider retaining its grant; moved/deleted/revoked files may no longer open.
- Saved file history begins with this version and retains up to 1,000 file records. The latest 2,000 messages are shown. Older app versions did not persist file links; this update cannot reconstruct them. NOLOG attachment metadata is not persisted.
- High-importance notifications request heads-up alerts; Android notification permission, channel settings, Do Not Disturb and launcher/OS policies remain authoritative. Message previews are off by default and lock-screen visibility is private.
- The Home-screen widget requires a launcher with widget support. Connect/Stop is user initiated; there is no boot auto-start.
- Remove device blocks its IPv4 address and revokes active file offers. Restoring or deliberately adding that IP enables it again. DHCP address reuse may require restoring an address. IP blocking does not authenticate peers.
- Optional secure-only controls preserve RSA/AES interoperability but can reject older/plaintext peers. Classic IPMsg file-body encryption has no integrity tag, UDP discovery/ACKs can be spoofed and first-use keys still need fingerprint verification.
- Phone Wi-Fi transfers, camera/audio quality and OEM camera apps, physical Home-screen launchers, prolonged Doze/battery restrictions and multi-GB transfers need physical-device acceptance. No physical phone is attached.

## Existing transfer fix

The v0.3 fix remains: a regular download completes after the advertised byte count and flush/close, without waiting for Windows EOF. Empty and truncated files are handled separately. A Windows-style sender waiting for receiver closure is covered by socket regression tests. If a prior Windows offer was already consumed, send a fresh offer before retrying.

Historical evidence is retained in docs/VALIDATION-v0.2.0.md and docs/VALIDATION-v0.3.0.md. The captured Windows 5.9.1 encrypted-message fixture still passes; this is not a new physical Windows-to-phone test.
