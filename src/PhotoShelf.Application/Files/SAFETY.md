# File operation safety contract

`FileMoveService` changes filesystem paths only. It never decodes or re-encodes media and never edits embedded metadata.

## Public API and caller obligations

- `Plan(...)` returns immutable individual entries with `GroupId`. Preview every member, including sidecars, before requesting execution. Optional `layout: MoveLayoutOptions` selects destination/year/year-month/year-month-day folders, capture-date filename prefixes, and a validated event folder. Missing capture dates are never fabricated. Optional `token:` cancels planning. Each source directory is indexed once per preview.
- `ExecuteAsync(...)` creates a new journal with `FileMode.CreateNew`; existing journals are never overwritten.
- `ReadHistory(directory)` returns completed, pending and undone counts, errors and an inverse-journal link.
- `ReadPlan(journalPath)` reads the verified journal, exposes source/destination/size/hash/group for the confirmation UI.
- `RecoverAsync(...)` resumes a previously confirmed operation. It may repeat the catalog callback after an uncertain commit.
- `UndoAsync(original, inverse, ...)` creates an independently recoverable inverse operation. Occupied original paths or changed destination content block the whole associated group.
- The catalog callback MUST reconcile source -> destination transactionally, durably and idempotently. Use a SQLite transaction and `synchronous=FULL` on its connection. It must handle a repeat after the transaction already committed.
- A `Moved=false` result does not imply that the file is still at its original path. The original bytes may already be safely at the target and need catalog reconciliation. Offer recovery, not blind retry with a new plan.
- Run all planning, history reads, hashing, execution and recovery off the WPF dispatcher. Cancellation is honored during preparation/copy and between groups. Once group catalog commit starts, cancellation is deferred until that group reaches a safe boundary.
- Preserve journals. Do not delete `.photoshelf-moving-*`, `.photoshelf-copy-*` or `.photoshelf-retained-*` files as cache files.

## Durable state protocol

1. Validate names, snapshots, reparse points and all group members.
2. Hash each original. Persist one checksummed, flushed group manifest containing all member identities and unique staging paths before changing any file.
3. Rename originals to staging with no overwrite and no copy fallback.
4. Same-volume transfer: a native no-replace rename, followed by hash verification at the destination. Windows uses `MoveFileExW` with `MOVEFILE_WRITE_THROUGH`, without `COPY_ALLOWED` or `REPLACE_EXISTING`.
5. Cross-volume transfer: create a unique temporary copy, flush it to disk, preserve creation/modification timestamps, flush metadata, re-read both source and copy, compare SHA-256, durably publish the destination without overwrite, and verify the final path again. Record the actual destination timestamp (FAT/exFAT may round it). Source snapshot checks stay strict; undo restores the recorded original timestamp where the filesystem can represent it.
6. Hold all verified group destinations open with sharing that denies writes/deletes during catalog commit and source cleanup.
7. Persist catalog checkpoints. On Windows, open the staging original with `GENERIC_READ | DELETE` and only read sharing, verify its bytes again, and mark THAT HANDLE for deletion using `FILE_DISPOSITION_INFO`. There is no path-based source deletion.
8. Persist completion. Recovery is repeatable. Unknown or changed files are retained.

Every journal record has a sequence and SHA-256 chain. A torn final record is preserved separately and recovery resumes from the verified prefix. Complete corrupted records stop automatic recovery. A group manifest is indivisible for journal parsing, so an interruption before staging cannot produce a partial companion plan.

## Groups and conservative refusals

- Unique RAW+JPEG with the same basename and exact XMP/AAE sidecars are grouped. Rename suffixes remain aligned across members.
- Same-basename photo+MOV is ambiguous without a verified content identifier. It is blocked unless the caller supplies `MoveRequest.ConfirmedCompanions` from a proven relation.
- Reparse points/symlinks/mounted-directory traversal are refused until an explicit physical-path workflow exists.
- Cross-volume copying of EFS-encrypted files or files containing additional NTFS data streams is refused. Same-volume rename preserves them. This avoids silently dropping streams or encryption.
- Legacy v0.9.7 journals lack sufficient integrity/snapshot information for automatic recovery and are shown as requiring manual inspection.
- Partial or corrupt temporary copies are retained. Recovery allocates a new temporary name; it does not silently remove uncertain data.

## Platform and validation limits

PhotoShelf is a Windows application. Portable macOS/Linux logic tests do not execute Windows sharing/disposition/NTFS primitives. Those checks have explicit `WindowsFact` gates and must pass on Windows before claiming native runtime validation.

POSIX unlink cannot atomically delete an already verified open file object. Therefore the portable fallback retains its original under a journaled backup name instead of performing an unsafe path-based unlink. This fallback is primarily for portable tests; it deliberately does not reclaim source storage. Unix renames also flush directory entries.

Logical fault injection checks exceptions and interruption boundaries. `PhotoShelf.CrashProbe` additionally runs the real engine in an isolated subprocess and pauses at a flushed checkpoint; its parent kills the process without managed cleanup, then validates surviving bytes, recovery, catalog reconciliation and an idempotent repeat. Six portable process-kill cases cover copy staging/verification/publishing/catalog commit/source cleanup and native-rename publication. Two separately gated Windows process-kill cases exercise the locked deletion handle and completed native deletion. These tests do not simulate physical power removal, faulty storage firmware or loss of the only device. `Flush(true)` and Windows write-through are OS durability requests; independent backups are still required against device loss. Tests use synthetic files only.

Win32 reference contracts:
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirststreamw
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ns-fileapi-win32_find_stream_data
