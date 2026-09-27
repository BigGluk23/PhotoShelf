using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using System.IO;

namespace PhotoShelf.Infrastructure.Sqlite;

public sealed class SqliteDesktopCatalogStore
{
    private readonly string _connectionString;
    private readonly string _directory;

    public SqliteDesktopCatalogStore(string? catalogDirectory = null)
    {
        _directory = catalogDirectory ?? LocalCatalogStore.CatalogDirectory;
        var databasePath = Path.Combine(_directory, "catalog-v2.sqlite");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS desktop_media_items (
                path TEXT NOT NULL PRIMARY KEY,
                is_favorite INTEGER NOT NULL DEFAULT 0,
                size_bytes INTEGER NOT NULL,
                file_modified_utc_ticks INTEGER NOT NULL,
                is_video INTEGER NOT NULL DEFAULT 0,
                last_seen_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS desktop_settings (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS desktop_excluded_folders (
                path TEXT NOT NULL PRIMARY KEY
            );
            CREATE INDEX IF NOT EXISTS ix_desktop_media_items_modified
                ON desktop_media_items(file_modified_utc_ticks);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalCatalogState> LoadAsync(CancellationToken cancellationToken = default, bool includeItems = true)
    {
        var state = new LocalCatalogState();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        if (includeItems)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT path, is_favorite FROM desktop_media_items ORDER BY file_modified_utc_ticks DESC;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                state.Items.Add(new SavedMediaItem
                {
                    Path = reader.GetString(0),
                    IsFavorite = reader.GetInt64(1) == 1
                });
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT path FROM desktop_excluded_folders ORDER BY path;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                state.ExcludedFolders.Add(reader.GetString(0));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key, value FROM desktop_settings;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = reader.GetString(0);
                var value = reader.GetString(1);
                state.ReadItemsFromSqlite = true;
                switch (key)
                {
                    case "include_system": state.IncludeSystemFolders = bool.Parse(value); break;
                    case "active_folder": state.ActiveFolder = string.IsNullOrEmpty(value) ? null : value; break;
                    case "view_mode": state.ViewMode = value; break;
                    case "newest_first": state.SortNewestFirst = bool.Parse(value); break;
                    case "include_subfolders": state.IncludeSubfolders = bool.Parse(value); break;
                    case "expanded": state.ExpandedFolders = System.Text.Json.JsonSerializer.Deserialize<List<string>>(value) ?? new(); break;
                }
                if (key == "tile_width" && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tileWidth))
                {
                    state.TileWidth = tileWidth;
                }
                else if (key == "show_videos" && bool.TryParse(value, out var showVideos))
                {
                    state.ShowVideos = showVideos;
                }
                else if (key == "date_grouping_mode")
                {
                    state.DateGroupingMode = value;
                }
            }
        }

        return state;
    }

    // Called by the catalog worker; each callback is awaited to bound dispatcher pressure.
    public async Task ReadBatchesAsync(Func<IReadOnlyList<SavedMediaItem>, Task> apply, CancellationToken token)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, is_favorite, size_bytes, file_modified_utc_ticks FROM desktop_media_items ORDER BY file_modified_utc_ticks DESC, path;";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var batch = new List<SavedMediaItem>(128);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            batch.Add(new SavedMediaItem { Path = reader.GetString(0), IsFavorite = reader.GetInt64(1) == 1,
                SizeBytes = reader.GetInt64(2), FileModifiedAt = reader.GetInt64(3) == 0 ? null : new DateTime(reader.GetInt64(3), DateTimeKind.Utc).ToLocalTime() });
            if (batch.Count < 128) continue;
            await apply(batch.ToArray()).ConfigureAwait(false);
            batch.Clear();
        }
        if (batch.Count > 0) await apply(batch.ToArray()).ConfigureAwait(false);
    }

    public async Task SaveAsync(LocalCatalogState state, CancellationToken cancellationToken = default, bool saveItems = true)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (saveItems) await ExecuteAsync(connection, transaction, "DELETE FROM desktop_media_items;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM desktop_excluded_folders;", cancellationToken).ConfigureAwait(false);

        foreach (var item in saveItems ? state.Items : Enumerable.Empty<SavedMediaItem>())
        {
            if (string.IsNullOrWhiteSpace(item.Path))
            {
                continue;
            }

            
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO desktop_media_items(path, is_favorite, size_bytes, file_modified_utc_ticks, is_video, last_seen_utc)
                VALUES ($path, $favorite, $size, $modified, $video, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            command.Parameters.AddWithValue("$path", item.Path);
            command.Parameters.AddWithValue("$favorite", item.IsFavorite ? 1 : 0);
            command.Parameters.AddWithValue("$size", item.SizeBytes);
            command.Parameters.AddWithValue("$modified", item.FileModifiedAt?.ToUniversalTime().Ticks ?? 0);
            command.Parameters.AddWithValue("$video", item.IsVideo ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var folder in state.ExcludedFolders.Where(static folder => !string.IsNullOrWhiteSpace(folder)))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT OR REPLACE INTO desktop_excluded_folders(path) VALUES ($path);";
            command.Parameters.AddWithValue("$path", folder);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await UpsertSettingAsync(connection, transaction, "tile_width", state.TileWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await UpsertSettingAsync(connection, transaction, "show_videos", state.ShowVideos.ToString(), cancellationToken).ConfigureAwait(false);
        await UpsertSettingAsync(connection, transaction, "date_grouping_mode", state.DateGroupingMode, cancellationToken).ConfigureAwait(false);

        await UpsertSettingAsync(connection, transaction, "include_system", state.IncludeSystemFolders.ToString(), cancellationToken);
        await UpsertSettingAsync(connection, transaction, "active_folder", state.ActiveFolder ?? "", cancellationToken);
        await UpsertSettingAsync(connection, transaction, "view_mode", state.ViewMode, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "newest_first", state.SortNewestFirst.ToString(), cancellationToken);
        await UpsertSettingAsync(connection, transaction, "include_subfolders", state.IncludeSubfolders.ToString(), cancellationToken);
        await UpsertSettingAsync(connection, transaction, "expanded", System.Text.Json.JsonSerializer.Serialize(state.ExpandedFolders), cancellationToken);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveItemAsync(string source, string destination, bool removeFromLibrary = false)
    {
        await using var connection = await OpenAsync(CancellationToken.None);
        await using var transaction = connection.BeginTransaction();
        foreach (var table in new[] { "desktop_media_items", "desktop_metadata_cache", "duplicate_hash_cache" })
        {
            await using var exists = connection.CreateCommand(); exists.Transaction = transaction;
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$table";
            exists.Parameters.AddWithValue("$table", table);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync()) == 0) continue;
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = removeFromLibrary ? $"DELETE FROM {table} WHERE path=$source;" : $"UPDATE {table} SET path=$destination WHERE path=$source;";
            command.Parameters.AddWithValue("$source", source);
            if (!removeFromLibrary) command.Parameters.AddWithValue("$destination", destination);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task UpsertSettingAsync(SqliteConnection connection, SqliteTransaction transaction, string key, string value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO desktop_settings(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
