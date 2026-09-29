using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Metadata;

namespace PhotoShelf.Infrastructure.Sqlite;

public sealed partial class SqliteDesktopCatalogStore
{
    private const int CurrentSchemaVersion = 5;
    private const string LegacyImportMarker = "legacy_json_import";
    private static readonly string[] LegacyColumns = ["path", "is_favorite", "size_bytes", "file_modified_utc_ticks", "is_video", "last_seen_utc"];
    private static readonly string[] PagedColumns = ["asset_id", "path_key", "folder_key", "search_key", "file_local_ticks", "file_month",
        "capture_date_ticks", "capture_month", "metadata_indexed", "is_hidden_or_system", "is_quarantined"];
    private static readonly string[] MetadataColumns = ["metadata_status", "metadata_attempted_ticks", "metadata_retry_ticks", "metadata_error_code"];
    private static readonly string[] ObservationColumns = ["availability", "availability_checked_ticks", "availability_error_code", "file_identity", "observation_version"];
    private static readonly string[] EstablishedSettings = ["tile_width", "show_videos", "date_grouping_mode", "include_system",
        "active_folder", "view_mode", "newest_first", "include_subfolders", "expanded"];

    /// <summary>Read-only compatibility check, also usable before publishing a copied storage generation.</summary>
    public Task ValidateCompatibilityAsync(CancellationToken token = default) => Task.Run(() =>
        CatalogDatabaseAccess.WriteAsync(_directory, () => ValidateCompatibilityCoreAsync(token, validatePathKeys: true), token), token);

    private async Task ValidateCompatibilityCoreAsync(CancellationToken token, bool validatePathKeys, bool requirePublishedGeneration = false)
    {
        var path = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new InvalidDataException("Catalog database path points to a directory.");
        }
        catch (FileNotFoundException) when (!requirePublishedGeneration) { return; }
        catch (DirectoryNotFoundException) when (!requirePublishedGeneration) { return; }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { throw new InvalidDataException("Опубликованный каталог отсутствует. Новая пустая база не создана; требуется восстановление прежнего каталога.", error); }
        var options = new SqliteConnectionStringBuilder(_connectionString) { Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(options.ToString());
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) tables.Add(reader.GetString(0));
        int? version = null;
        var completedImport = false;
        if (tables.Contains("desktop_settings"))
        {
            command.CommandText = "SELECT value FROM desktop_settings WHERE key='catalog_schema';";
            var value = await command.ExecuteScalarAsync(token);
            if (value is not null)
            {
                if (value is not string text || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    || number is not (3 or 4 or CurrentSchemaVersion))
                    throw new InvalidDataException("This catalog uses an unsupported or newer schema; no migration or import was started.");
                version = number;
            }
            command.CommandText = "SELECT value FROM desktop_settings WHERE key=$key;";
            command.Parameters.AddWithValue("$key", LegacyImportMarker);
            var importMarker = await command.ExecuteScalarAsync(token);
            if (importMarker is not null && (importMarker is not string completed || !IsCompletedImport(completed)))
                throw new InvalidDataException("Unknown legacy import state; no migration or import was started.");
            completedImport = importMarker is string known && IsCompletedImport(known);
            command.Parameters.Clear();
        }
        if (requirePublishedGeneration && (version != CurrentSchemaVersion || !tables.Contains("desktop_media_items") || !completedImport))
            throw new InvalidDataException("Опубликованный каталог неполон или повреждён. Новая пустая база и повторный импорт не созданы; требуется восстановление прежнего каталога.");
        if (!tables.Contains("desktop_media_items"))
        {
            // Empty SQLite files and the old auxiliary stores can precede the first catalog initialization.
            if (version is not null || tables.Except(["desktop_settings", "desktop_excluded_folders", "desktop_metadata_cache", "duplicate_hash_cache"]).Any())
                throw new InvalidDataException("Unrecognized catalog layout; no migration or import was started.");
            return;
        }
        command.CommandText = "PRAGMA table_info(desktop_media_items);";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) columns.Add(reader.GetString(1));
        if (version is null)
        {
            // The original v0.9.7 writer had no schema marker. Do not guess an arbitrary newer layout.
            if (!columns.SetEquals(LegacyColumns))
                throw new InvalidDataException("Catalog schema marker is missing and its layout is not a known legacy version.");
        }
        else
        {
            var required = LegacyColumns.Concat(PagedColumns)
                .Concat(version >= 4 ? MetadataColumns : []).Concat(version >= 5 ? ObservationColumns : []);
            if (required.Any(column => !columns.Contains(column)))
                throw new InvalidDataException("Catalog schema marker does not match its columns; migration was not started.");
        }
        if (validatePathKeys && columns.Contains("path_key") && columns.Contains("folder_key"))
        {
            // A pre-paged writer can update path without its newer keys. SQLite's
            // integrity_check cannot detect that semantic inconsistency. Never guess
            // an identity repair or activate a catalog whose path lookup is already broken.
            connection.CreateFunction("ps_guard_path_key", (string? value) => value is null ? null : NormalizePathKey(value));
            connection.CreateFunction("ps_guard_folder_key", (string? value) => value is null ? null : FolderKey(value));
            command.CommandText = """
                SELECT EXISTS(SELECT 1 FROM desktop_media_items WHERE path IS NULL OR path_key IS NULL OR folder_key IS NULL
                    OR path_key COLLATE BINARY<>ps_guard_path_key(path) OR folder_key COLLATE BINARY<>ps_guard_folder_key(path));
                """;
            using var interruption = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
            long inconsistent;
            try { inconsistent = Convert.ToInt64(await command.ExecuteScalarAsync(token)); }
            catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            if (inconsistent != 0)
                throw new InvalidDataException("В каталоге нарушено соответствие путей и ключей папок. Обновление остановлено для сохранности данных; исходный каталог не изменён. Требуется ручной разбор, автоматическая замена путей не выполнялась.");
        }
    }

    /// <summary>
    /// Makes SQLite authoritative without ever rewriting the legacy JSON. Missing rows,
    /// missing preferences, reusable hashes and the completion marker commit together.
    /// </summary>
    public async Task InitializeAndImportLegacyAsync(string legacyJsonPath, CancellationToken token = default,
        bool requirePublishedGeneration = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyJsonPath);
        // This explicit startup/activation boundary validates every persisted path key.
        // Ordinary InitializeAsync only checks schema, avoiding repeated full-table scans.
        await InitializeCoreAsync(token, validatePathKeys: true, requirePublishedGeneration).ConfigureAwait(false);
        await Task.Run(() => CatalogDatabaseAccess.WriteAsync(_directory, async () =>
        {
            await using var connection = await OpenAsync(token);
            var marker = await ReadSettingAsync(connection, null, LegacyImportMarker, token);
            if (marker is not null)
            {
                if (!IsCompletedImport(marker)) throw new InvalidDataException("Unknown legacy import state; no catalog data was replaced.");
                return;
            }
            var established = await IsEstablishedCatalogAsync(connection, token);
            var legacy = established ? null : await LocalCatalogStore.ReadLegacyAsync(legacyJsonPath, token);
            await using var transaction = connection.BeginTransaction();
            if (legacy is not null)
            {
                await ImportLegacyItemsAsync(connection, transaction, legacy.Items, token);
                await ImportLegacySettingsAsync(connection, transaction, legacy, token);
                await ImportLegacyHashesAsync(connection, transaction, legacy.DuplicateHashes, token);
            }
            await InsertSettingAsync(connection, transaction, LegacyImportMarker,
                established ? "existing-sqlite" : legacy is null ? "absent" : "completed", token);
            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(token);
        }, token), token).ConfigureAwait(false);
    }

    private static bool IsCompletedImport(string value) => value is "sqlite" or "existing-sqlite" or "absent" or "completed";

    private static async Task<string?> ReadSettingAsync(SqliteConnection connection, SqliteTransaction? transaction, string key, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT value FROM desktop_settings WHERE key=$key;"; command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(token) as string;
    }

    private static async Task<bool> IsEstablishedCatalogAsync(SqliteConnection connection, CancellationToken token)
    {
        // Old startup committed its complete preference set only after all JSON batches.
        // Some rows or an arbitrary setting alone can instead be an interrupted import.
        var completeSettings = true;
        foreach (var key in EstablishedSettings)
            if (await ReadSettingAsync(connection, null, key, token) is null) { completeSettings = false; break; }
        if (completeSettings) return true;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM desktop_move_receipts) OR EXISTS(SELECT 1 FROM desktop_media_items WHERE is_quarantined=1);";
        return Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 0;
    }

    private static async Task InsertSettingAsync(SqliteConnection connection, SqliteTransaction transaction, string key, string value, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO desktop_settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO NOTHING;";
        command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task ImportLegacyItemsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IEnumerable<SavedMediaItem> items, CancellationToken token)
    {
        var hasMetadataCache = await TableExistsAsync(connection, transaction, "desktop_metadata_cache", token);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO desktop_media_items(asset_id,path,path_key,folder_key,search_key,is_favorite,size_bytes,
                file_modified_utc_ticks,file_local_ticks,file_month,capture_date_ticks,capture_month,metadata_indexed,
                metadata_status,metadata_attempted_ticks,metadata_retry_ticks,metadata_error_code,is_video,is_hidden_or_system,last_seen_utc)
            VALUES($id,$path,$key,$folder,$key,$favorite,$size,$modified,$local,$month,$capture,$captureMonth,$indexed,
                $status,$attempted,$retry,$error,$video,$system,strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            ON CONFLICT(path_key) DO NOTHING;
            """;
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested(); command.Parameters.Clear();
            var local = item.FileModifiedAt is { Kind: DateTimeKind.Utc } utc ? utc.ToLocalTime() : item.FileModifiedAt;
            var status = item.MetadataStatus == MetadataReadStatus.Pending && item.MetadataIndexed
                ? item.CaptureDate is null ? MetadataReadStatus.Absent : MetadataReadStatus.Found : item.MetadataStatus;
            command.Parameters.AddWithValue("$id", string.IsNullOrEmpty(item.AssetId) ? Guid.NewGuid().ToString("N") : item.AssetId);
            command.Parameters.AddWithValue("$path", item.Path); command.Parameters.AddWithValue("$key", NormalizePathKey(item.Path));
            command.Parameters.AddWithValue("$folder", FolderKey(item.Path)); command.Parameters.AddWithValue("$favorite", item.IsFavorite ? 1 : 0);
            command.Parameters.AddWithValue("$size", item.SizeBytes); command.Parameters.AddWithValue("$modified", item.FileModifiedAt?.ToUniversalTime().Ticks ?? 0);
            command.Parameters.AddWithValue("$local", local?.Ticks ?? 0); command.Parameters.AddWithValue("$month", (object?)Month(local) ?? DBNull.Value);
            command.Parameters.AddWithValue("$capture", (object?)item.CaptureDate?.Ticks ?? DBNull.Value);
            command.Parameters.AddWithValue("$captureMonth", (object?)Month(item.CaptureDate) ?? DBNull.Value);
            command.Parameters.AddWithValue("$indexed", item.MetadataIndexed ? 1 : 0); command.Parameters.AddWithValue("$status", (int)status);
            command.Parameters.AddWithValue("$attempted", (object?)item.MetadataAttemptedAtUtc?.Ticks ?? DBNull.Value);
            command.Parameters.AddWithValue("$retry", (object?)item.MetadataRetryAtUtc?.Ticks ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)item.MetadataErrorCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$video", item.IsVideo ? 1 : 0); command.Parameters.AddWithValue("$system", item.IsHiddenOrSystem ? 1 : 0);
            var inserted = await command.ExecuteNonQueryAsync(token) == 1;
            if (!inserted || !hasMetadataCache || item.CaptureDate is not null || item.MetadataIndexed) continue;
            await using var cached = connection.CreateCommand(); cached.Transaction = transaction;
            cached.CommandText = "SELECT capture_date_ticks FROM desktop_metadata_cache WHERE path=$path AND size_bytes=$size AND file_modified_utc_ticks=$modified;";
            cached.Parameters.AddWithValue("$path", item.Path); cached.Parameters.AddWithValue("$size", item.SizeBytes);
            cached.Parameters.AddWithValue("$modified", item.FileModifiedAt?.ToUniversalTime().Ticks ?? 0);
            if (await cached.ExecuteScalarAsync(token) is not long captureTicks) continue;
            var capture = new DateTime(captureTicks);
            cached.CommandText = "UPDATE desktop_media_items SET capture_date_ticks=$capture,capture_month=$month,metadata_indexed=1,metadata_status=1 WHERE path=$path;";
            cached.Parameters.AddWithValue("$capture", captureTicks); cached.Parameters.AddWithValue("$month", Month(capture)!);
            await cached.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task ImportLegacySettingsAsync(SqliteConnection connection, SqliteTransaction transaction, LocalCatalogState state, CancellationToken token)
    {
        var values = new Dictionary<string, string>
        {
            ["tile_width"] = state.TileWidth.ToString(CultureInfo.InvariantCulture), ["show_videos"] = state.ShowVideos.ToString(),
            ["date_grouping_mode"] = state.DateGroupingMode, ["include_system"] = state.IncludeSystemFolders.ToString(),
            ["active_folder"] = state.ActiveFolder ?? "", ["view_mode"] = state.ViewMode, ["newest_first"] = state.SortNewestFirst.ToString(),
            ["include_subfolders"] = state.IncludeSubfolders.ToString(), ["expanded"] = JsonSerializer.Serialize(state.ExpandedFolders),
            ["background_processing_paused"] = state.BackgroundProcessingPaused.ToString(),
            ["included_folders"] = JsonSerializer.Serialize(state.IncludedFolders), ["watched_folders"] = JsonSerializer.Serialize(state.WatchedFolders),
            ["quarantine_directory"] = state.QuarantineDirectory ?? "", ["quarantine_batches"] = JsonSerializer.Serialize(state.QuarantineBatchDirectories)
        };
        foreach (var pair in values) await InsertSettingAsync(connection, transaction, pair.Key, pair.Value, token);
        foreach (var folder in state.ExcludedFolders)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO desktop_excluded_folders(path) VALUES($path) ON CONFLICT(path) DO NOTHING;";
            command.Parameters.AddWithValue("$path", folder); await command.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task ImportLegacyHashesAsync(SqliteConnection connection, SqliteTransaction transaction,
        IEnumerable<SavedDuplicateHash> hashes, CancellationToken token)
    {
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS duplicate_hash_cache(path TEXT NOT NULL PRIMARY KEY,size_bytes INTEGER NOT NULL,
                file_modified_ticks INTEGER NOT NULL,sha256 TEXT NOT NULL,hashed_at_utc TEXT NOT NULL);
            """, token);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO duplicate_hash_cache(path,size_bytes,file_modified_ticks,sha256,hashed_at_utc)
            SELECT $path,$size,$local,$hash,strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE EXISTS(SELECT 1 FROM desktop_media_items WHERE path_key=$key AND path=$path
                AND size_bytes=$size AND file_modified_utc_ticks=$utc AND is_quarantined=0)
            ON CONFLICT(path) DO NOTHING;
            """;
        foreach (var hash in hashes)
        {
            token.ThrowIfCancellationRequested();
            // Invalid derived hashes are never promoted into trusted reusable entries.
            if (hash is null || string.IsNullOrWhiteSpace(hash.Path) || hash.FileModifiedAt is null
                || hash.SizeBytes < 0 || hash.Hash is null || hash.Hash.Length != 64 || hash.Hash.Any(c => !Uri.IsHexDigit(c))) continue;
            command.Parameters.Clear(); command.Parameters.AddWithValue("$path", hash.Path);
            command.Parameters.AddWithValue("$key", NormalizePathKey(hash.Path)); command.Parameters.AddWithValue("$size", hash.SizeBytes);
            command.Parameters.AddWithValue("$local", hash.FileModifiedAt.Value.Ticks);
            command.Parameters.AddWithValue("$utc", hash.FileModifiedAt.Value.ToUniversalTime().Ticks); command.Parameters.AddWithValue("$hash", hash.Hash);
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
