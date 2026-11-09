# Focused v0.3 freeze regression checks

The packaged `run-headless.ps1` was also executed against the current production sources: **20 checks passed**, comprising the nine backend checks below and eleven native local file-drop/prepared-cache checks. The script used only its generated temporary fake runtime and removed its artifacts. The optional physical harness was not invoked by this rerun.

Console-only checks run against a compiled fake `adb.exe`. No personal phone, real ADB session, or application window was used.

## Reproduction against saved v0.2 bridge

`ProbeOld.exe` runs a fake ADB command with a 1,000 ms deadline. The command exits immediately after emitting `parent-finished`; a helper inherits its output handles for 12 seconds.

Observed: **not completed after 5,146 ms**, probe exit code **1**. The old bridge disables its deadline after process exit, then closes a reader with an outstanding read and awaits pipe EOF without a bound.

## Fixed bridge

The same probe returned `parent-finished` in **446 ms**, exit code **0**.

The regression runner completed **9 checks**, zero failures:

1. Inherited output handles: bounded snapshot in 439 ms, explicitly marked incomplete.
2. Simultaneous stdout/stderr beyond pipe capacity: drained without deadlock, each bounded to 256 KiB.
3. Unresponsive ADB: 350 ms deadline completed in 374 ms.
4. Unresponsive ADB: cancellation completed in 279 ms.
5. Prepared-file single flight: a cancelled second waiter did not cancel the owner or produce a second download.
6. Preparation on an unpumped UI-like synchronization context: entry returned in 0 ms; cancellation completed and removed its partial.
7. Cancelled preparation could be retried with the same selected file.
8. Download deadline completed and cleaned its owned partial.
9. Changed source metadata failed before a pull began.

These isolate process handling, preparation, cancellation and timeout behavior. They do not establish physical-phone throughput or Windows Explorer interoperability; the separate local-file drag implementation addresses the former network-during-OLE path.

Implementation keeps output collectors alive until EOF instead of synchronously closing a pending Framework pipe read. Deferred cleanup can retain the two reader tasks/handles while an external descendant retains inherited handles; callers never await that cleanup. ADB process startup is explicitly off the UI thread. Structured-output consumers reject snapshots without EOF.
