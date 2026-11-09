# Contributing

Thanks for helping improve IPMsg Phone Dock. Keep changes local-first, explicit about sharing, and compatible with the paired release versions.

1. Open an issue for a bug or proposed behavior change. For security-sensitive reports, see `SECURITY.md`.
2. Keep Windows and Android wire changes in sync; include or update focused tests.
3. Do not commit signing keys, lineage files, local Android SDK paths, user logs, or personal device data.
4. Include the Windows and Android versions used for interoperability reports. Physical-device results should be distinguished from emulators and host tests.
5. Run the relevant component checks described in `windows/checks/README.md` and `android/TESTING.md`.

Pull requests should explain the user-visible change, safety boundaries, and validation performed. Avoid claims such as “zero lag” or “secure” without measurements and a clear threat model.
