# PLAN-008: Atomic Crash-Safe File Writes & Startup Recovery

- **Task ID**: `TASK-013`
- **Fixes Issue**: [`ISS-013`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
In `IOService.FlushFileToDisk`:
```csharp
File.WriteAllBytes(file.Path, serializedBytes);
```
`File.WriteAllBytes` opens the existing `.xod`, `.xtab`, `.xpag`, or `.xidx` file with `FileMode.Create` (which immediately truncates the existing file to `0` bytes) before writing the new buffer. If the process crashes, throws an unhandled I/O error, or loses power mid-write, the target file is left truncated or partially written, corrupting the database.

## Design & Refactoring Steps
1. In `IOService.FlushFileToDisk(XFile file)`:
   - Write the serialized (and optionally AES-256-GCM encrypted) bytes to a temporary sibling file `file.Path + ".tmp"` using a `FileStream` with `FileMode.Create, FileAccess.Write, FileShare.None`.
   - Call `fs.Flush(flushToDisk: true)` to ensure the OS write buffer is committed before replacing the live file.
   - Atomically replace the target file using `File.Move(tempPath, file.Path, overwrite: true)`.
2. In `IOService` constructor (startup recovery):
   - Scan the database directory for any leftover `*.tmp` files from an interrupted write:
     - If the corresponding target file exists, delete the incomplete `.tmp` file.
     - If the target file does not exist (interrupted during initial creation), promote or clean up the `.tmp` file safely.
3. In `IndexService.TryLoadFromDisk`:
   - Verify that `.xidx` is not older than the newest `.xpag` file for that table; if a crash occurred after `.xpag` was written but before `.xidx` was flushed, automatically rebuild `.xidx` from `.xpag` on cold start.

## Verification Plan
- Add crash-recovery & fault-injection torture test `Crash_Recovery_Orphan_Tmp_And_Stale_Index_Self_Heal_On_Startup` in `XodStressTests.cs`.
