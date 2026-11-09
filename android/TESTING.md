# IPMsg Android 0.8.1 RC1 validation

The shrunk Android release variant built successfully with Gradle 8.11.1. The APK metadata reports application ID `app.lantern.lan`, version code 15 and version name `0.8.1`. The local release output is unsigned; it must be signed with the existing private key and verified lineage before installation as an update.

The complete host-side `testDebugUnitTest` suite passed **80 tests, 0 failures, 0 errors**. The focused folder/wire suite also passed **27 cases** for bounded archives, traversal, cycles, empty/nested/Unicode content, changed providers, low storage, cancellation, reserved MIME and stale/missing gesture acknowledgements. Those 27 are included in the 80-test full-suite count, not additive.

Windows local validation passed 160 focused checks across connection identity/recovery, folder extraction and saving, authenticated preparation, cache ownership, native FileDrop, presentation and cancellation. The portable payload manifest and Setup payload verification also passed.

These are source, JVM and generated-fixture checks. Physical acceptance remains pending: Android SAF/provider grants, process death during a provider read, signed in-place upgrade/history preservation, screen-off/hotspot transfers with mobile data disabled, reconnection after real USB/Wi-Fi changes, and actual folder/file drag into Windows Explorer. The UI build itself does not prove those device/OS behaviors.

Host unit tests and release compilation are not an external penetration test. Known limitations and remaining findings are in `REVIEW.md` and the Windows `SECURITY-REVIEW.md`. Do not treat successful archive extraction fixtures as proof that a particular Windows application accepts its drag payload.
