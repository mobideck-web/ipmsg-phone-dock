# Phone-to-PC drag regression checks

Run `./checks/run-headless.ps1 -Suite Drag` from the project root in PowerShell. This compiles the actual production
`VirtualFileDrag.cs` and `PreparedFiles.cs` together with isolated phone-transfer
metadata/delay doubles. It requires the installed .NET Framework compiler and
Windows interop libraries; it downloads nothing.

The checks inspect the real WinForms `DataObject` through its COM `IDataObject`
interface. Windows `DragQueryFileW` verifies the native `CF_HDROP` medium, Unicode
paths and file count. A sparse file larger than 4 GiB checks that the medium
contains paths rather than file contents, without allocating a large file buffer.

Other checks cover missing/empty/cancelled drags, refusal of the old deferred
download path, prepared-cache single-flight ownership, waiter cancellation,
owner cancellation/retry, local-copy edits and Windows filename handling.

The harness does not enter a mouse drag loop, create a window, drive Explorer,
connect a phone or run ADB. It does not establish physical-phone transfer success
or prove that a particular desktop target accepts the drop. Physical backend
results are recorded separately in `PhoneRegression-results.txt`; actual Explorer
drop interaction remains pending. In-drag Esc/cancellation is implemented with WinForms
`QueryContinueDrag`, but is not exercised by this headless harness.

`VirtualFileDrag` no longer supplies custom COM virtual files, synchronous
download callbacks, manual `DoEvents`, or blocking task waits. Phone files must
finish downloading before a normal Windows `FileDrop` begins. The legacy entry
point refuses immediately with recovery instructions and never calls its
download delegate.
