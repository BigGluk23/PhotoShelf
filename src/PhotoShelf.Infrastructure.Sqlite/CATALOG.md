# Working catalog in 0.10.0

`SqliteDesktopCatalogStore` is the authoritative store used by the Windows desktop. The retained table name `desktop_media_items` is a compatibility name; this is now a stable-identity catalog with an asset ID, normalized Windows path/folder keys, persisted file/capture dates, metadata state, quarantine state, and indexed paged queries. The older rich domain schema remains available for future metadata integrations; it is not populated as a second copy by the Desktop. Unifying the active read/write path avoids an untested rewrite of that unused schema.

`SavedMediaItem` is the bounded catalog DTO. The Desktop requests a group summary and pages; it must not call `LoadAsync(includeItems: true)` to populate a global UI collection. That overload remains only for legacy import/tool/test compatibility. Sequential background enumeration uses a keyset cursor; random viewport access uses indexed offsets, normally within a month. Filters, dates, folders, favorites, recent selection, and group counts are SQL operations. Cursor identity includes the view and uses the persisted SQL sort key.

Scans upsert in 256-row transactions and preserve favorites. A partial scan never implies deletion of missing or offline files. Favorites and metadata are explicit targeted updates; a stale metadata result must match the current path/size/mtime. All working stores share one writer gate. Every connection uses `synchronous=FULL`; the database uses WAL. Move callbacks return only after a transaction commits the stable identity, legacy metadata/hash paths, quarantine state, and idempotency receipt. Destination collisions stop reconciliation instead of replacing another asset. Reverse movement restores quarantined identity and user data.

The size-count table is maintained by SQLite triggers in the same transaction as the asset update. Duplicate candidate enumeration reads its partial index rather than repeatedly grouping the entire catalog. The hash cache uses size and modification time as its reuse fingerprint; file-operation safety must independently verify bytes before cleanup.

Before migrating v0.9.7, the application checks the source, creates a SQLite backup (including committed WAL pages), verifies it with `integrity_check`, flushes the completed file, and publishes its `.sqlite` name with a durable no-overwrite rename (Windows WRITE_THROUGH; Unix directory fsync). Until verification succeeds, the candidate has a `.pending` suffix. Failed/partial backups are retained rather than guessed safe to remove. Ambiguous normalized paths abort the migration transaction and preserve the old schema and rows. Manual backups use the same procedure.

Do not restore an old catalog while the application runs. Catalog snapshots are not copies of photo/video originals. Restoring an older catalog can also restore older move receipts; coordinate offline recovery with the file-operation journal and reconcile filesystem paths before further operations. The application therefore exposes verified backup creation, while automated restore of arbitrary old snapshots is deliberately not a file-operation recovery mechanism.

Tests cover migration rollback/backup integrity, Unicode path and date query parity, keyset/offset equivalence, filters, stale metadata suppression, quarantine restoration, duplicate counters, and bounded page access at 10,000 / 100,000 / 1,000,000 records. SQL timings are diagnostic measurements on the execution host, not WPF responsiveness measurements on Windows.

## Diagnostic run, 2026-09-27

.NET 10.0.12, macOS ARM64, temporary local SQLite databases, warm reads after ingest. These are individual SQL measurements, not Windows UI measurements or p95 guarantees. All three size tests passed; file operations and backup changes were checked separately with targeted regression tests.

| Records | Ingest with FULL commits | First 128 | Month counts | Final 128 by offset | Capture-date first 128 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 10,000 | 1,081 ms | 0.47 ms | 0.67 ms | 0.88 ms | 0.38 ms |
| 100,000 | 24,926 ms | 0.64 ms | 7.04 ms | 5.30 ms | 0.49 ms |
| 1,000,000 | 306,375 ms | 0.52 ms | 66.69 ms | 48.50 ms | 0.54 ms |

The first index draft required 1,401 ms for million-row groups and 1,166 ms for the final offset page. Query-plan inspection showed unnecessary table lookups and temporary grouping. Covering indexes reduced those costs without lowering `synchronous=FULL`. The ingest work remains background work in bounded committed batches.
