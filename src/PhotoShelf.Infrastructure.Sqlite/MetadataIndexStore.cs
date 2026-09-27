using Microsoft.Data.Sqlite;

namespace PhotoShelf.Infrastructure.Sqlite;

/// <summary>Legacy metadata import/cache adapter; the paged catalog stores the authoritative indexed date.</summary>
public sealed class MetadataIndexStore
{
    private readonly string _connectionString;
    private readonly string _directory;
    public MetadataIndexStore(string? catalogDirectory=null)
    {
        _directory=catalogDirectory??LocalCatalogStore.CatalogDirectory;
        _connectionString=CatalogDatabaseAccess.ConnectionString(_directory);
    }
    public Task InitializeAsync(CancellationToken cancellationToken=default)=>Task.Run(()=>CatalogDatabaseAccess.WriteAsync(_directory,async()=>
    {
        Directory.CreateDirectory(_directory);await using var connection=await OpenAsync(cancellationToken);
        await using var command=connection.CreateCommand();command.CommandText="""
            CREATE TABLE IF NOT EXISTS desktop_metadata_cache (
                path TEXT NOT NULL PRIMARY KEY,size_bytes INTEGER NOT NULL,file_modified_utc_ticks INTEGER NOT NULL,
                capture_date_ticks INTEGER NULL,metadata_indexed_at_utc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_desktop_metadata_cache_indexed ON desktop_metadata_cache(metadata_indexed_at_utc);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    },cancellationToken),cancellationToken);

    public Task<Dictionary<string,CachedCaptureDate>> LoadCaptureDatesAsync(CancellationToken cancellationToken=default)=>Task.Run(async()=>
    {
        await using var connection=await OpenAsync(cancellationToken);await using var command=connection.CreateCommand();
        var hasCatalog=await HasCatalogAsync(connection,null,cancellationToken);
        command.CommandText="SELECT path,capture_date_ticks,size_bytes,file_modified_utc_ticks FROM desktop_metadata_cache m"+
            (hasCatalog ? " WHERE NOT EXISTS(SELECT 1 FROM desktop_media_items i WHERE i.path=m.path AND i.is_quarantined=1)" : "")+";";
        var result=new Dictionary<string,CachedCaptureDate>(StringComparer.OrdinalIgnoreCase);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken))result[reader.GetString(0)]=new CachedCaptureDate(reader.GetInt64(2),reader.GetInt64(3),
            reader.IsDBNull(1) ? null : new DateTime(reader.GetInt64(1),DateTimeKind.Unspecified));return result;
    },cancellationToken);

    public Task SaveAsync(string path,long size,DateTime? modified,DateTime? captureDate,CancellationToken cancellationToken=default)=>Task.Run(()=>CatalogDatabaseAccess.WriteAsync(_directory,async()=>
    {
        await using var connection=await OpenAsync(cancellationToken);await using var transaction=connection.BeginTransaction();
        await using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            INSERT INTO desktop_metadata_cache(path,size_bytes,file_modified_utc_ticks,capture_date_ticks,metadata_indexed_at_utc)
            VALUES($path,$size,$modified,$capture,strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            ON CONFLICT(path) DO UPDATE SET size_bytes=excluded.size_bytes,file_modified_utc_ticks=excluded.file_modified_utc_ticks,
                capture_date_ticks=excluded.capture_date_ticks,metadata_indexed_at_utc=excluded.metadata_indexed_at_utc;
            """;
        command.Parameters.AddWithValue("$path",path);command.Parameters.AddWithValue("$size",size);
        command.Parameters.AddWithValue("$modified",modified?.ToUniversalTime().Ticks??0);
        command.Parameters.AddWithValue("$capture",(object?)captureDate?.Ticks??DBNull.Value);await command.ExecuteNonQueryAsync(cancellationToken);
        if(await HasCatalogAsync(connection,transaction,cancellationToken))
        {
            command.CommandText="""
                UPDATE desktop_media_items SET capture_date_ticks=$capture,capture_month=$month,metadata_indexed=1
                WHERE path_key=$key AND size_bytes=$size AND file_modified_utc_ticks=$modified AND is_quarantined=0;
                """;
            command.Parameters.AddWithValue("$key",SqliteDesktopCatalogStore.NormalizePathKey(path));
            command.Parameters.AddWithValue("$month",(object?)captureDate?.ToString("yyyy-MM",System.Globalization.CultureInfo.InvariantCulture)??DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    },cancellationToken),cancellationToken);

    private static async Task<bool> HasCatalogAsync(SqliteConnection connection,SqliteTransaction? transaction,CancellationToken token)
    {
        await using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE name='desktop_media_items');";
        return Convert.ToInt64(await command.ExecuteScalarAsync(token))==1;
    }
    private Task<SqliteConnection> OpenAsync(CancellationToken token)=>CatalogDatabaseAccess.OpenAsync(_connectionString,token);
}

public sealed record CachedCaptureDate(long Size,long ModifiedTicks,DateTime? CaptureDate);
