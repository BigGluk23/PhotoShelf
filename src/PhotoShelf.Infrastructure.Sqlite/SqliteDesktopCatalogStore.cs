using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using System.Globalization;
using System.Text.Json;

namespace PhotoShelf.Infrastructure.Sqlite;

/// <summary>Desktop's authoritative catalog. All SQLite work runs on workers; writes share one gate.</summary>
public sealed partial class SqliteDesktopCatalogStore
{
    private readonly string _connectionString;
    private readonly string _directory;
    public SqliteDesktopCatalogStore(string? catalogDirectory = null)
    {
        _directory = catalogDirectory ?? LocalCatalogStore.CatalogDirectory;
        _connectionString = CatalogDatabaseAccess.ConnectionString(_directory);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
        CatalogDatabaseAccess.WriteAsync(_directory, async () =>
        {
            Directory.CreateDirectory(_directory);
            await using var connection = await OpenAsync(cancellationToken);
            var exists = await TableExistsAsync(connection, null, "desktop_media_items", cancellationToken);
            var hasSizeCounts = await TableExistsAsync(connection, null, "desktop_size_counts", cancellationToken);
            var migrated = false;
            var hasMetadataStatus = false;
            var hasObservations = false;
            if (exists)
            {
                await using var info = connection.CreateCommand();
                info.CommandText = "PRAGMA table_info(desktop_media_items);";
                await using var reader = await info.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.GetString(1) == "asset_id") migrated = true;
                    if (reader.GetString(1) == "metadata_status") hasMetadataStatus = true;
                    if (reader.GetString(1) == "availability") hasObservations = true;
                }
            }
            // SQLite backup includes committed WAL pages. Never copy the main file alone.
            if (exists && !migrated) await CreateVerifiedBackupAsync(connection, "before-paged-schema", cancellationToken);
            else if (exists && !hasMetadataStatus) await CreateVerifiedBackupAsync(connection, "before-metadata-status", cancellationToken);
            else if (exists && !hasObservations) await CreateVerifiedBackupAsync(connection, "before-file-observations", cancellationToken);
            await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken);
            await using var transaction = connection.BeginTransaction();
            if (exists && !migrated)
                await ExecuteAsync(connection, transaction, "ALTER TABLE desktop_media_items RENAME TO desktop_media_items_v097;", cancellationToken);
            await ExecuteAsync(connection, transaction, Schema, cancellationToken);
            if (exists && migrated && !hasMetadataStatus)
                await ExecuteAsync(connection, transaction, """
                    ALTER TABLE desktop_media_items ADD COLUMN metadata_status INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE desktop_media_items ADD COLUMN metadata_attempted_ticks INTEGER NULL;
                    ALTER TABLE desktop_media_items ADD COLUMN metadata_retry_ticks INTEGER NULL;
                    ALTER TABLE desktop_media_items ADD COLUMN metadata_error_code TEXT NULL;
                    """, cancellationToken);
            if (exists && migrated && !hasObservations)
                await ExecuteAsync(connection, transaction, """
                    ALTER TABLE desktop_media_items ADD COLUMN availability INTEGER NOT NULL DEFAULT 4;
                    ALTER TABLE desktop_media_items ADD COLUMN availability_checked_ticks INTEGER NULL;
                    ALTER TABLE desktop_media_items ADD COLUMN availability_error_code TEXT NULL;
                    ALTER TABLE desktop_media_items ADD COLUMN file_identity TEXT NULL;
                    ALTER TABLE desktop_media_items ADD COLUMN observation_version INTEGER NOT NULL DEFAULT 0;
                    """, cancellationToken);
            if (exists && migrated && !hasSizeCounts)
                await ExecuteAsync(connection, transaction, "INSERT INTO desktop_size_counts(size_bytes,item_count) SELECT size_bytes,COUNT(*) FROM desktop_media_items WHERE is_quarantined=0 GROUP BY size_bytes;", cancellationToken);
            if (exists && !migrated)
            {
                connection.CreateFunction("ps_path_key", (string p) => NormalizePathKey(p));
                connection.CreateFunction("ps_folder_key", (string p) => FolderKey(p));
                connection.CreateFunction("ps_local_ticks", (long ticks) => ticks == 0 ? 0 : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().Ticks);
                connection.CreateFunction("ps_month", (long ticks) => ticks == 0 ? null : Month(new DateTime(ticks, DateTimeKind.Utc).ToLocalTime()));
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO desktop_media_items(asset_id,path,path_key,folder_key,search_key,is_favorite,size_bytes,
                        file_modified_utc_ticks,file_local_ticks,file_month,is_video,last_seen_utc)
                    SELECT lower(hex(randomblob(16))),path,ps_path_key(path),ps_folder_key(path),ps_path_key(path),is_favorite,
                        size_bytes,file_modified_utc_ticks,ps_local_ticks(file_modified_utc_ticks),ps_month(file_modified_utc_ticks),is_video,last_seen_utc
                    FROM desktop_media_items_v097;
                    DROP TABLE desktop_media_items_v097;
                    """, cancellationToken);
                if (await TableExistsAsync(connection, transaction, "desktop_metadata_cache", cancellationToken))
                {
                    connection.CreateFunction("ps_capture_month", (long ticks) => Month(new DateTime(ticks)));
                    await ExecuteAsync(connection, transaction, """
                        UPDATE desktop_media_items SET
                        capture_date_ticks=(SELECT m.capture_date_ticks FROM desktop_metadata_cache m WHERE m.path=desktop_media_items.path),
                        capture_month=(SELECT ps_capture_month(m.capture_date_ticks) FROM desktop_metadata_cache m WHERE m.path=desktop_media_items.path AND m.capture_date_ticks IS NOT NULL),
                        metadata_indexed=1
                        WHERE EXISTS(SELECT 1 FROM desktop_metadata_cache m WHERE m.path=desktop_media_items.path
                            AND m.size_bytes=desktop_media_items.size_bytes AND m.file_modified_utc_ticks=desktop_media_items.file_modified_utc_ticks);
                        """, cancellationToken);
                }
            }
            // Older nulls could mean a swallowed decoder error. Recheck them once, preserve known dates.
            if (!hasMetadataStatus)
                await ExecuteAsync(connection, transaction, """
                    UPDATE desktop_media_items SET metadata_status=CASE WHEN capture_date_ticks IS NOT NULL THEN 1 ELSE 0 END,
                        metadata_indexed=CASE WHEN capture_date_ticks IS NOT NULL THEN 1 ELSE 0 END;
                    """, cancellationToken);
            await ExecuteAsync(connection, transaction, """
                CREATE UNIQUE INDEX IF NOT EXISTS ix_desktop_path_key ON desktop_media_items(path_key);
                CREATE INDEX IF NOT EXISTS ix_desktop_file_sort ON desktop_media_items(is_quarantined,file_local_ticks DESC,path_key,is_hidden_or_system,is_video,is_favorite);
                CREATE INDEX IF NOT EXISTS ix_desktop_file_sort_asc ON desktop_media_items(is_quarantined,file_local_ticks ASC,path_key,is_hidden_or_system,is_video,is_favorite);
                CREATE INDEX IF NOT EXISTS ix_desktop_capture_sort ON desktop_media_items(is_quarantined,COALESCE(capture_date_ticks,0) DESC,path_key,is_hidden_or_system,is_video,is_favorite);
                CREATE INDEX IF NOT EXISTS ix_desktop_capture_sort_asc ON desktop_media_items(is_quarantined,COALESCE(capture_date_ticks,0) ASC,path_key,is_hidden_or_system,is_video,is_favorite);
                CREATE INDEX IF NOT EXISTS ix_desktop_folder ON desktop_media_items(folder_key,file_local_ticks DESC,path_key);
                CREATE INDEX IF NOT EXISTS ix_desktop_folder_path ON desktop_media_items(folder_key,path_key);
                CREATE INDEX IF NOT EXISTS ix_desktop_file_group ON desktop_media_items(is_quarantined,file_month,file_local_ticks DESC,path_key,is_hidden_or_system,is_video,is_favorite);
                CREATE INDEX IF NOT EXISTS ix_desktop_capture_group ON desktop_media_items(is_quarantined,capture_month,COALESCE(capture_date_ticks,0) DESC,path_key,is_hidden_or_system,is_video,is_favorite);
                CREATE INDEX IF NOT EXISTS ix_desktop_metadata_due ON desktop_media_items(metadata_indexed,is_quarantined,is_video,metadata_retry_ticks);
                CREATE INDEX IF NOT EXISTS ix_desktop_size ON desktop_media_items(size_bytes,is_quarantined);
                CREATE INDEX IF NOT EXISTS ix_desktop_favorites ON desktop_media_items(is_favorite,file_local_ticks DESC,path_key);
                CREATE INDEX IF NOT EXISTS ix_desktop_file_identity ON desktop_media_items(file_identity) WHERE file_identity IS NOT NULL;
                INSERT INTO desktop_settings(key,value) VALUES('catalog_schema','5') ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken), cancellationToken);

    public Task<LocalCatalogState> LoadAsync(CancellationToken cancellationToken = default, bool includeItems = true) => Task.Run(async () =>
    {
        var state = new LocalCatalogState();
        await using var connection = await OpenAsync(cancellationToken);
        if (includeItems)
        {
            await using var command = connection.CreateCommand(); command.CommandText = $"SELECT {ItemColumns} FROM desktop_media_items WHERE is_quarantined=0 ORDER BY file_local_ticks DESC,path_key;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) state.Items.Add(ReadItem(reader));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path FROM desktop_excluded_folders ORDER BY path;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) state.ExcludedFolders.Add(reader.GetString(0));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key,value FROM desktop_settings;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.GetString(0); var value = reader.GetString(1);
                if (key != "catalog_schema") state.ReadItemsFromSqlite = true;
                switch (key)
                {
                    case "include_system": if (bool.TryParse(value, out var system)) state.IncludeSystemFolders = system; break;
                    case "active_folder": state.ActiveFolder = string.IsNullOrEmpty(value) ? null : value; break;
                    case "view_mode": state.ViewMode = value; break;
                    case "newest_first": if (bool.TryParse(value, out var newest)) state.SortNewestFirst = newest; break;
                    case "include_subfolders": if (bool.TryParse(value, out var subfolders)) state.IncludeSubfolders = subfolders; break;
                    case "expanded": state.ExpandedFolders = JsonSerializer.Deserialize<List<string>>(value) ?? new(); break;
                    case "watched_folders": state.WatchedFolders = JsonSerializer.Deserialize<List<string>>(value) ?? new(); break;
                    case "included_folders": state.IncludedFolders = JsonSerializer.Deserialize<List<string>>(value) ?? new(); break;
                    case "tile_width": if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)) state.TileWidth = width; break;
                    case "show_videos": if (bool.TryParse(value, out var videos)) state.ShowVideos = videos; break;
                    case "date_grouping_mode": state.DateGroupingMode = value; break;
                    case "quarantine_directory": state.QuarantineDirectory = string.IsNullOrWhiteSpace(value) ? null : value; break;
                    case "quarantine_batches": state.QuarantineBatchDirectories = JsonSerializer.Deserialize<List<string>>(value) ?? new(); break;
                }
            }
        }
        return state;
    }, cancellationToken);

    /// <summary>Compatibility import: add/update given items, never infer deletion from an incomplete view.</summary>
    public async Task SaveAsync(LocalCatalogState state, CancellationToken cancellationToken = default, bool saveItems = true)
    {
        if (saveItems) await UpsertItemsAsync(state.Items, preserveFavorites: false, cancellationToken);
        await Task.Run(() => CatalogDatabaseAccess.WriteAsync(_directory, async () =>
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, "DELETE FROM desktop_excluded_folders;", cancellationToken);
            foreach (var folder in state.ExcludedFolders.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT OR IGNORE INTO desktop_excluded_folders(path) VALUES($path);";
                command.Parameters.AddWithValue("$path", folder); await command.ExecuteNonQueryAsync(cancellationToken);
            }
            var values = new Dictionary<string,string>
            {
                ["tile_width"] = state.TileWidth.ToString(CultureInfo.InvariantCulture), ["show_videos"] = state.ShowVideos.ToString(),
                ["date_grouping_mode"] = state.DateGroupingMode, ["include_system"] = state.IncludeSystemFolders.ToString(),
                ["active_folder"] = state.ActiveFolder ?? "", ["view_mode"] = state.ViewMode, ["newest_first"] = state.SortNewestFirst.ToString(),
                ["include_subfolders"] = state.IncludeSubfolders.ToString(), ["expanded"] = JsonSerializer.Serialize(state.ExpandedFolders),
                ["included_folders"] = JsonSerializer.Serialize(state.IncludedFolders),
                ["watched_folders"] = JsonSerializer.Serialize(state.WatchedFolders)
            };
            foreach (var (key,value) in values)
            {
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO desktop_settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
                command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$value",value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken), cancellationToken);
    }

    public async Task ReadBatchesAsync(Func<IReadOnlyList<SavedMediaItem>, Task> apply, CancellationToken token)
    {
        var query = new CatalogViewQuery { IncludeSystemFolders = true, PageSize = 128 };
        do
        {
            token.ThrowIfCancellationRequested();
            var page = await QueryPageAsync(query, token);
            if (page.Items.Count > 0) await apply(page.Items);
            token.ThrowIfCancellationRequested();
            if (!page.HasMore) break;
            query = query with { Cursor = page.NextCursor };
        } while (true);
    }

    public Task<string> CreateBackupAsync(CancellationToken token=default)=>Task.Run(async()=>
    {
        await CatalogDatabaseAccess.Gate(_directory).WaitAsync(token);
        try
        {
            await using var connection=await OpenAsync(token);
            return await CreateVerifiedBackupAsync(connection,"manual",token);
        }
        finally {CatalogDatabaseAccess.Gate(_directory).Release();}
    },token);

    private async Task<string> CreateVerifiedBackupAsync(SqliteConnection source, string purpose, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using (var check = source.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check;";
            if (!Equals(await check.ExecuteScalarAsync(token), "ok")) throw new InvalidDataException("Catalog integrity check failed; migration was not started.");
        }
        var backups = Path.Combine(_directory, "backups"); Directory.CreateDirectory(backups);
        var path = Path.Combine(backups, $"catalog-{purpose}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sqlite");
        var pendingPath = path + ".pending";
        await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=pendingPath, Pooling=false }.ToString()))
        {
            await destination.OpenAsync(token); source.BackupDatabase(destination);
            await using var check = destination.CreateCommand(); check.CommandText = "PRAGMA integrity_check;";
            if (!Equals(await check.ExecuteScalarAsync(token), "ok")) throw new InvalidDataException("Catalog backup verification failed; migration was not started.");
        }
        using (var file = new FileStream(pendingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            file.Flush(flushToDisk: true);
        token.ThrowIfCancellationRequested();
        CatalogBackupPublication.Complete(pendingPath, path); // Completed backups alone have the .sqlite extension.
        return path;
    }

    private Task<SqliteConnection> OpenAsync(CancellationToken token) => CatalogDatabaseAccess.OpenAsync(_connectionString, token);
    internal static string NormalizePathKey(string path) => path.Replace('\\','/').TrimEnd('/').ToUpperInvariant();
    private static string FolderKey(string path) { var normalized = NormalizePathKey(path); var slash=normalized.LastIndexOf('/'); return slash < 0 ? "" : normalized[..slash]; }
    private static string? Month(DateTime? date) => date?.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    private static async Task<bool> TableExistsAsync(SqliteConnection c, SqliteTransaction? t, string name, CancellationToken token)
    {
        await using var command=c.CreateCommand(); command.Transaction=t;
        command.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";
        command.Parameters.AddWithValue("$name",name); return Convert.ToInt64(await command.ExecuteScalarAsync(token))==1;
    }
    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction? t, string sql, CancellationToken token)
    {
        await using var command=c.CreateCommand(); command.Transaction=t; command.CommandText=sql; await command.ExecuteNonQueryAsync(token);
    }
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS desktop_media_items (
            asset_id TEXT NOT NULL PRIMARY KEY, path TEXT NOT NULL UNIQUE, path_key TEXT NOT NULL UNIQUE,
            folder_key TEXT NOT NULL, search_key TEXT NOT NULL, is_favorite INTEGER NOT NULL DEFAULT 0,
            size_bytes INTEGER NOT NULL, file_modified_utc_ticks INTEGER NOT NULL, file_local_ticks INTEGER NOT NULL DEFAULT 0,
            file_month TEXT NULL, capture_date_ticks INTEGER NULL, capture_month TEXT NULL,
            metadata_indexed INTEGER NOT NULL DEFAULT 0, metadata_status INTEGER NOT NULL DEFAULT 0,
            metadata_attempted_ticks INTEGER NULL, metadata_retry_ticks INTEGER NULL, metadata_error_code TEXT NULL, is_video INTEGER NOT NULL DEFAULT 0,
            is_hidden_or_system INTEGER NOT NULL DEFAULT 0, is_quarantined INTEGER NOT NULL DEFAULT 0, last_seen_utc TEXT NOT NULL,
            availability INTEGER NOT NULL DEFAULT 4, availability_checked_ticks INTEGER NULL, availability_error_code TEXT NULL,
            file_identity TEXT NULL, observation_version INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS desktop_size_counts(size_bytes INTEGER NOT NULL PRIMARY KEY,item_count INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_desktop_duplicate_sizes ON desktop_size_counts(size_bytes) WHERE item_count>1;
        CREATE TRIGGER IF NOT EXISTS desktop_size_insert AFTER INSERT ON desktop_media_items WHEN NEW.is_quarantined=0
        BEGIN
            INSERT INTO desktop_size_counts(size_bytes,item_count) VALUES(NEW.size_bytes,1)
            ON CONFLICT(size_bytes) DO UPDATE SET item_count=item_count+1;
        END;
        CREATE TRIGGER IF NOT EXISTS desktop_size_delete AFTER DELETE ON desktop_media_items WHEN OLD.is_quarantined=0
        BEGIN
            UPDATE desktop_size_counts SET item_count=item_count-1 WHERE size_bytes=OLD.size_bytes;
            DELETE FROM desktop_size_counts WHERE size_bytes=OLD.size_bytes AND item_count=0;
        END;
        CREATE TRIGGER IF NOT EXISTS desktop_size_update AFTER UPDATE OF size_bytes,is_quarantined ON desktop_media_items
        WHEN OLD.size_bytes<>NEW.size_bytes OR OLD.is_quarantined<>NEW.is_quarantined
        BEGIN
            UPDATE desktop_size_counts SET item_count=item_count-1 WHERE size_bytes=OLD.size_bytes AND OLD.is_quarantined=0;
            INSERT INTO desktop_size_counts(size_bytes,item_count) SELECT NEW.size_bytes,1 WHERE NEW.is_quarantined=0
            ON CONFLICT(size_bytes) DO UPDATE SET item_count=item_count+1;
            DELETE FROM desktop_size_counts WHERE size_bytes=OLD.size_bytes AND item_count=0;
        END;
        CREATE TABLE IF NOT EXISTS desktop_settings(key TEXT NOT NULL PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS desktop_excluded_folders(path TEXT NOT NULL PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS desktop_move_receipts(
            source_key TEXT NOT NULL,destination_key TEXT NOT NULL,quarantined INTEGER NOT NULL,
            asset_id TEXT NULL,committed_at_utc TEXT NOT NULL,PRIMARY KEY(source_key,destination_key,quarantined)
        );
        """;
}
