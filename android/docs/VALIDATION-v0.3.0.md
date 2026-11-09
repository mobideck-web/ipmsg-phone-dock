# Lantern LAN 0.3.0 validation

Test date: November 5, 2026. Development test APK, Android 10+.

## Build and unit checks

- Final debug APK and Android instrumentation APK compile with the existing Gradle 8.11.1 / AGP 8.9.1 / Kotlin 2.1.20 / Android SDK 35 configuration.
- All 25 JVM tests pass: the previous 20 protocol/crypto/network-safety cases plus five transfer regression tests.
- The new tests cover a Windows-style sender that sends complete 2 MiB+17 byte APK/ZIP payloads and waits for receiver EOF; zero-byte transfers without a socket read; truncated input with exact byte counts; AES-CTR data across uneven 777-byte chunks; cancellation and storage failure propagation.
- Android lint passes with zero errors. Existing advisory warnings remain, including deprecated system-bar setters and dependency/target suggestions.
- APK signature verification passes. The certificate matches v0.2.0, so installing the update with the same package preserves application data.

## Android runtime checks

All six test cases passed on Android 15 across two runs: four engine/database tests in the initial run and both UI tests in a separate rerun (40.244 seconds). The first UI attempts were blocked by emulator Process system/System UI ANR dialogs under host memory pressure; they are not counted as passes. The dialogs were dismissed before the successful UI rerun.

The runtime suite verifies actual local UDP/TCP sockets, Android Keystore, signatures, spoofed identity/unsigned downgrade rejection, retries and duplicate suppression, plaintext/encrypted zero-byte and 256 KiB files, Windows-style receiver-close completion, truncated transfers, cancellation, expiry and replay persistence. The new database test checks v2 to v3 migration, unread state after reopening, mark-read/delete isolation between peer identities and replay protection after clearing a conversation. Both v1 migration and the prior draft restoration/search test also pass. The new UI test exercises device search/clear search, unread-to-read persistence, clear confirmation/cancellation and Android Back navigation.

The APK was installed over the emulator's existing v0.2.0 installation successfully. No app source changed after the final build/runtime checks; documentation was updated to record results.

The final UI smoke check passes all four tabs, listening start/stop and actual connected-device foreground service status. Light and dark screenshots of v0.3.0 were captured and visually checked, including the original IPMsg logo, new search field and system-bar contrast.

## Evidence and practical limits

The Windows reference implementation's src/mainwinfile.cpp SendFileThread enters complete_wait after writing a regular file, then waits for receiver socket closure. Lantern v0.2.0 instead read an extra byte to require sender EOF, producing a completion deadlock and Read timed out. The fix uses the protocol's declared length as completion, flushes the destination, then closes the socket. Truncated streams remain failures.

After a v0.2.0 timeout, Windows may already consider the old offer downloaded when the phone closes its socket. Ask Windows to send a new offer when retesting; retrying that old offer can return no file bytes. An early EOF can also result from a stopped sender, changed source or network interruption. This update does not label incomplete files as successful.

No physical phone is connected. Recheck the user's reported APK/ZIP transfers with fresh Windows offers over the actual Wi-Fi network and compare hashes. Long background/Doze operation, reconnection under manufacturer battery restrictions and multi-GB Wi-Fi transfers remain physical-device acceptance work.

The baseline Windows 5.9.1 messaging and crypto checks are documented in docs/VALIDATION-v0.2.0.md. The captured Windows encrypted-message fixture still passes. This release's Windows-style transfer regression verifies the completion contract using real sockets, not a physical Windows-to-phone session.

## Run locally

```powershell
.\gradlew.bat :app:assembleDebug :app:testDebugUnitTest :app:assembleDebugAndroidTest :app:lintDebug
adb install -r .\app\build\outputs\apk\debug\app-debug.apk
adb install -r .\app\build\outputs\apk\androidTest\debug\app-debug-androidTest.apk
adb shell am instrument -w -r app.lantern.lan.test/androidx.test.runner.AndroidJUnitRunner
```

Install over the existing Lantern APK without uninstalling. Stop any other Android IPMsg app first because it also listens on port 2425. Saved external files remain in their chosen folders; test downloads clean up partial files where the document provider permits.
