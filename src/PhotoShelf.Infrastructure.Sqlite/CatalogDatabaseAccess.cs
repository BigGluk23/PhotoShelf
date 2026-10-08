using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace PhotoShelf.Infrastructure.Sqlite;

internal static class CatalogDatabaseAccess
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public static SemaphoreSlim Gate(string directory) => Gates.GetOrAdd(Path.GetFullPath(directory), _ => new(1, 1));

    public static string ConnectionString(string directory) => new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(directory, "catalog-v2.sqlite"), Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private, Pooling = true, DefaultTimeout = 10
    }.ToString();

    public static async Task<SqliteConnection> OpenAsync(string connectionString, CancellationToken token,
        bool useWriteCache = false)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            // A small page cache spills heavily while maintaining multiple indexes.
            // Only the serialized bounded upsert gets a 32 MiB suggested page budget.
            // Reset on every checkout: pooled writer settings must not spread to readers.
            // This does not change WAL, FULL durability, spill policy or transaction size.
            command.CommandText = "PRAGMA busy_timeout=10000; PRAGMA synchronous=FULL; " +
                (useWriteCache ? "PRAGMA cache_size=-32768;" : "PRAGMA cache_size=-2000;");
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public static async Task WriteAsync(string directory, Func<Task> write, CancellationToken token)
    {
        await Gate(directory).WaitAsync(token).ConfigureAwait(false);
        try { await write().ConfigureAwait(false); }
        finally { Gate(directory).Release(); }
    }
}
