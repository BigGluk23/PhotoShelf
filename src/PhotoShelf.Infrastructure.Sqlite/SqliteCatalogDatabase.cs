using System.Reflection;
using Microsoft.Data.Sqlite;

namespace PhotoShelf.Infrastructure.Sqlite;

public sealed class SqliteCatalogDatabase
{
    private const string MigrationPrefix = "PhotoShelf.Infrastructure.Sqlite.Migrations.";
    private readonly string _connectionString;

    public SqliteCatalogDatabase(SqliteCatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var databasePath = options.GetValidatedFullPath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at_utc TEXT NOT NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);

        var assembly = typeof(SqliteCatalogDatabase).Assembly;
        var migrationNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(MigrationPrefix, StringComparison.Ordinal) &&
                           name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var resourceName in migrationNames)
        {
            var version = GetMigrationVersion(resourceName);
            if (await IsMigrationAppliedAsync(connection, version, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await ApplyMigrationAsync(connection, assembly, resourceName, version, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        Assembly assembly,
        string resourceName,
        int version,
        CancellationToken cancellationToken)
    {
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            command.Parameters.Clear();
            command.CommandText =
                "INSERT INTO schema_migrations(version, name, applied_at_utc) " +
                "VALUES ($version, $name, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));";
            command.Parameters.AddWithValue("$version", version);
            command.Parameters.AddWithValue("$name", resourceName[MigrationPrefix.Length..]);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static int GetMigrationVersion(string resourceName)
    {
        var fileName = resourceName[MigrationPrefix.Length..];
        var separatorIndex = fileName.IndexOf('_');
        if (separatorIndex <= 0 || !int.TryParse(fileName[..separatorIndex], out var version))
        {
            throw new InvalidOperationException($"Migration '{resourceName}' has an invalid version prefix.");
        }

        return version;
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        SqliteConnection connection,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version = $version);";
        command.Parameters.AddWithValue("$version", version);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result) == 1;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
