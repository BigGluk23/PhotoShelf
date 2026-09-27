using System.Text.Json;

namespace PhotoShelf.Infrastructure.Sqlite;

public sealed partial class SqliteDesktopCatalogStore
{
    // Separate writes prevent an ordinary UI-state save from replacing quarantine history with stale state.
    public Task SetQuarantineDirectoryAsync(string directory) => WriteQuarantineSettingAsync(directory, registerBatch: false);
    public Task RegisterQuarantineBatchAsync(string directory) => WriteQuarantineSettingAsync(directory, registerBatch: true);

    private Task WriteQuarantineSettingAsync(string directory, bool registerBatch)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Quarantine path must be absolute.", nameof(directory));
        directory = Path.GetFullPath(directory);
        return Task.Run(() => CatalogDatabaseAccess.WriteAsync(_directory, async () =>
        {
            await using var connection = await OpenAsync(CancellationToken.None);
            await using var transaction = connection.BeginTransaction();
            var key = registerBatch ? "quarantine_batches" : "quarantine_directory";
            var value = directory;
            if (registerBatch)
            {
                await using var read = connection.CreateCommand(); read.Transaction = transaction;
                read.CommandText = "SELECT value FROM desktop_settings WHERE key=$key;";
                read.Parameters.AddWithValue("$key", key);
                var previous = await read.ExecuteScalarAsync() as string;
                var roots = previous is null ? new List<string>() : JsonSerializer.Deserialize<List<string>>(previous)
                    ?? throw new InvalidDataException("Invalid quarantine history; it was not replaced.");
                if (!roots.Contains(directory, StringComparer.OrdinalIgnoreCase)) roots.Add(directory);
                value = JsonSerializer.Serialize(roots);
            }
            await using var write = connection.CreateCommand(); write.Transaction = transaction;
            write.CommandText = "INSERT INTO desktop_settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
            write.Parameters.AddWithValue("$key", key); write.Parameters.AddWithValue("$value", value);
            await write.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }, CancellationToken.None));
    }
}
