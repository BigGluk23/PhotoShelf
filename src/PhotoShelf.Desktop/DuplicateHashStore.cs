using Microsoft.Data.Sqlite;
using System.IO;

namespace PhotoShelf.Desktop;

public sealed class DuplicateHashStore
{
    private readonly string _connectionString;

    public DuplicateHashStore()
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
            CREATE TABLE IF NOT EXISTS duplicate_hash_cache (
                path TEXT NOT NULL PRIMARY KEY,
                size_bytes INTEGER NOT NULL,
                file_modified_ticks INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                hashed_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_duplicate_hash_cache_sha256
                ON duplicate_hash_cache(size_bytes, sha256);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SavedDuplicateHash>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<SavedDuplicateHash>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, size_bytes, file_modified_ticks, sha256 FROM duplicate_hash_cache;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var ticks = reader.GetInt64(2);
            result.Add(new SavedDuplicateHash
            {
                Path = reader.GetString(0),
                SizeBytes = reader.GetInt64(1),
                FileModifiedAt = ticks <= 0 ? null : new DateTime(ticks, DateTimeKind.Local),
                Hash = reader.GetString(3)
            });
        }

        return result;
    }

    public async Task SaveAsync(IEnumerable<SavedDuplicateHash> hashes, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var hash in hashes)
        {
            if (string.IsNullOrWhiteSpace(hash.Path) || string.IsNullOrWhiteSpace(hash.Hash))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO duplicate_hash_cache(path, size_bytes, file_modified_ticks, sha256, hashed_at_utc)
                VALUES ($path, $size, $modified, $hash, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                ON CONFLICT(path) DO UPDATE SET
                    size_bytes = excluded.size_bytes,
                    file_modified_ticks = excluded.file_modified_ticks,
                    sha256 = excluded.sha256,
                    hashed_at_utc = excluded.hashed_at_utc;
                """;
            command.Parameters.AddWithValue("$path", hash.Path);
            command.Parameters.AddWithValue("$size", hash.SizeBytes);
            command.Parameters.AddWithValue("$modified", hash.FileModifiedAt?.Ticks ?? 0L);
            command.Parameters.AddWithValue("$hash", hash.Hash);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
