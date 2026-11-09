# IPMsg Android 0.8.1 RC1 — review

The 0.8.1 change adds an explicit, temporary folder handoff to Phone Dock and hardens gesture acknowledgements and session ownership. The release APK compiles with shrinking, and the host JVM suite passes. This is a release candidate, not a claim of physical-device acceptance or independent security certification.

## Improved

- Folder sharing starts only from Android's system folder-tree picker. The app snapshots the selected tree locally, retains empty/nested/Unicode entries, and applies count, depth, size, deadline and storage limits. It does not request broad storage access or persist the selected folder grant.
- The PC reconstructs the selected archive as an ordinary directory and exposes that local completed path through Windows FileDrop. Folder entries are validated before extraction; writes are staged and tracked so cleanup targets only app-owned paths.
- Stale/missing gesture IDs and revisions no longer clear a newer selection. Session owner checks are repeated inside the insertion transaction to close a revocation race.
- The old custom COM drag broker and unused prepared-file browser were removed from the production build and retained only as historical fixtures. The supported Windows drag path starts with complete local files/folders, keeping network operations outside the drag callback.
- Stale ADB transports no longer immediately mark the selected phone offline. Windows checks remembered transports for the expected device identity and does not silently switch to an unrelated phone.

## Remaining risks and limits

- Folder transfer is snapshot-based, not streaming. The full folder is read on the phone, downloaded and extracted before a real folder drag is ready. Large trees therefore need time and temporary space. There is no cross-process resume.
- Android provider contents can change between checks and reads. The implementation detects common changes but cannot prove that provider data is immutable or authenticated.
- Certain names, case-only conflicts, reparse/link forms, and long paths are deliberately rejected to avoid unsafe or lossy Windows extraction. Android and Windows must both run the updated versions for folders to appear as folders.
- If Windows is killed while saving a folder to a user-selected destination, a hidden `.ipmsg-folder-<id>.part` directory can remain there. It is not claimed complete and is deliberately excluded from broad automatic cleanup.
- Connection tests use fake ADB endpoints. They do not demonstrate hotspot discovery or transport switching on a real phone, and the Windows archive fixtures do not demonstrate that Explorer accepts a drag in the user's live desktop.
- Camera audio, older chat/security behavior and Android system UI code remain in the product. The build emits deprecated system-bar color API warnings; these are not runtime validation. The test compilation also reports a nullable-string warning in `DockOwnershipTest`.
- Android 0.8.1's compiled release APK is unsigned in this workspace. It can update an existing installation only after the owner signs it with the existing key and lineage.

## Verification

- Android shrunk release variant: build successful; version code 15/name 0.8.1.
- Android host JVM: 80 tests passed, no failures/errors. The focused folder/wire suite has 27 cases and is included in that total.
- Windows: 160 focused local checks passed; setup payload hash verification passed.
- Physical phone, actual folder picker/grants, screen-off transfer, hotspot with mobile data disabled, installer upgrade, and real Explorer drag remain pending.
