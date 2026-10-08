using Microsoft.Data.Sqlite;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class CatalogConnectionProfileTests
{
    [Fact]
    public async Task PooledWriterKeepsFullDurabilityAndResetsPageBudgetForReaders()
    {
        var root = Directory.CreateTempSubdirectory("photoshelf-connection-profile-").FullName;
        var connectionString = CatalogDatabaseAccess.ConnectionString(root);
        try
        {
            await new SqliteDesktopCatalogStore(root).InitializeAsync();
            await using (var writer = await CatalogDatabaseAccess.OpenAsync(connectionString, CancellationToken.None, useWriteCache: true))
            {
                Assert.Equal(-32768L, await NumberAsync(writer, "PRAGMA cache_size;"));
                Assert.Equal(2L, await NumberAsync(writer, "PRAGMA synchronous;"));
                await using var command = writer.CreateCommand();
                command.CommandText = "PRAGMA journal_mode;";
                Assert.Equal("wal", await command.ExecuteScalarAsync());
                command.CommandText = "CREATE TEMP TABLE cache_profile_witness(value INTEGER); INSERT INTO cache_profile_witness VALUES(1);";
                await command.ExecuteNonQueryAsync();
            }
            await using var reader = await CatalogDatabaseAccess.OpenAsync(connectionString, CancellationToken.None);
            // The TEMP witness proves reuse of the same native connection, not a fresh
            // connection that would trivially have the default cache budget.
            Assert.Equal(1L, await NumberAsync(reader, "SELECT value FROM cache_profile_witness;"));
            Assert.Equal(-2000L, await NumberAsync(reader, "PRAGMA cache_size;"));
            Assert.Equal(2L, await NumberAsync(reader, "PRAGMA synchronous;"));
        }
        finally
        {
            using var pool = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(pool);
            Directory.Delete(root, true);
        }
    }

    private static async Task<long> NumberAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
