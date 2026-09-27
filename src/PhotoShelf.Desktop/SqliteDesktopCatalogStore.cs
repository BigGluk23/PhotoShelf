using Microsoft.Data.Sqlite;
using System.IO;

namespace PhotoShelf.Desktop;

public sealed class SqliteDesktopCatalogStore
{
    private readonly string _connectionString;

    public SqliteDesktopCatalogStore()
    {
        Directory.CreateDirectory(LocalCatalogStore.CatalogDirectory);
        var databasePath = Path.Combine(LocalCatalogStore.CatalogDirectory, "catalog-v2.sqlite");
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
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
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

    public async Task<LocalCatalogState> LoadAsync(CancellationToken cancellationToken = default)
    {
        var state = new LocalCatalogState();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
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
                if (key == "tile_width" && double.TryParse(value, out var tileWidth))
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

    public async Task SaveAsync(LocalCatalogState state, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, "DELETE FROM desktop_media_items;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM desktop_excluded_folders;", cancellationToken).ConfigureAwait(false);

        foreach (var item in state.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Path))
            {
                continue;
            }

            var file = new FileInfo(item.Path);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO desktop_media_items(path, is_favorite, size_bytes, file_modified_utc_ticks, is_video, last_seen_utc)
                VALUES ($path, $favorite, $size, $modified, $video, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            command.Parameters.AddWithValue("$path", item.Path);
            command.Parameters.AddWithValue("$favorite", item.IsFavorite ? 1 : 0);
            command.Parameters.AddWithValue("$size", file.Exists ? file.Length : 0);
            command.Parameters.AddWithValue("$modified", file.Exists ? file.LastWriteTimeUtc.Ticks : 0);
            command.Parameters.AddWithValue("$video", PhotoItem.IsVideoPath(item.Path) ? 1 : 0);
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

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
