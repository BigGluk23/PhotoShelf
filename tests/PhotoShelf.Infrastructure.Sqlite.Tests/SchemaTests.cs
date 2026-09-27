using Microsoft.Data.Sqlite;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class SchemaTests
{
    [Fact]
    public async Task InitialMigrationIsIdempotentAndHasExpectedMetadataTables()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"photoshelf-test-{Guid.NewGuid():N}.db");

        try
        {
            var database = new SqliteCatalogDatabase(new SqliteCatalogOptions(databasePath));
            await database.InitializeAsync();
            await database.InitializeAsync();

            await using var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True");
            await connection.OpenAsync();

            var tables = await ReadNamesAsync(
                connection,
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';");

            Assert.Contains("normalized_metadata", tables);
            Assert.Contains("metadata_properties", tables);
            Assert.Contains("metadata_field_sources", tables);
            Assert.Contains("metadata_unique_ids", tables);
            Assert.Contains("asset_files", tables);

            await using var foreignKeyCheck = connection.CreateCommand();
            foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
            await using var violations = await foreignKeyCheck.ExecuteReaderAsync();
            Assert.False(await violations.ReadAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteIfExists(databasePath);
            DeleteIfExists(databasePath + "-wal");
            DeleteIfExists(databasePath + "-shm");
        }
    }

    private static async Task<HashSet<string>> ReadNamesAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var names = new HashSet<string>(StringComparer.Ordinal);

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
