using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using System.IO;

namespace PhotoShelf.Infrastructure.Sqlite;

public sealed class MetadataIndexStore
{
    private readonly string _connectionString;
    private readonly string _directory;

    public MetadataIndexStore(string? catalogDirectory = null)
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
            CREATE TABLE IF NOT EXISTS desktop_metadata_cache (
                path TEXT NOT NULL PRIMARY KEY,
                size_bytes INTEGER NOT NULL,
                file_modified_utc_ticks INTEGER NOT NULL,
                capture_date_ticks INTEGER NULL,
                metadata_indexed_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_desktop_metadata_cache_indexed
                ON desktop_metadata_cache(metadata_indexed_at_utc);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, CachedCaptureDate>> LoadCaptureDatesAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, CachedCaptureDate>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, capture_date_ticks, size_bytes, file_modified_utc_ticks FROM desktop_metadata_cache;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var path = reader.GetString(0);
            var captureDate = reader.IsDBNull(1) ? (DateTime?)null : new DateTime(reader.GetInt64(1), DateTimeKind.Local);
            result[path] = new CachedCaptureDate(reader.GetInt64(2), reader.GetInt64(3), captureDate);
        }

        return result;
    }

    public async Task SaveAsync(string path, long size, DateTime? modified, DateTime? captureDate, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO desktop_metadata_cache(
                path,
                size_bytes,
                file_modified_utc_ticks,
                capture_date_ticks,
                metadata_indexed_at_utc)
            VALUES ($path, $size, $modified, $capture, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            ON CONFLICT(path) DO UPDATE SET
                size_bytes = excluded.size_bytes,
                file_modified_utc_ticks = excluded.file_modified_utc_ticks,
                capture_date_ticks = excluded.capture_date_ticks,
                metadata_indexed_at_utc = excluded.metadata_indexed_at_utc;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$modified", modified?.ToUniversalTime().Ticks ?? 0L);
        command.Parameters.AddWithValue("$capture", captureDate is null ? DBNull.Value : captureDate.Value.Ticks);
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

public sealed record CachedCaptureDate(long Size, long ModifiedTicks, DateTime? CaptureDate);
