using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;

namespace PhotoShelf.Infrastructure.Sqlite;

public sealed class DuplicateHashStore
{
    private readonly string _connectionString;
    private readonly string _directory;
    public DuplicateHashStore(string? catalogDirectory=null)
    {
        _directory=catalogDirectory??LocalCatalogStore.CatalogDirectory;
        _connectionString=CatalogDatabaseAccess.ConnectionString(_directory);
    }
    public Task InitializeAsync(CancellationToken cancellationToken=default)=>Task.Run(()=>CatalogDatabaseAccess.WriteAsync(_directory,async()=>
    {
        Directory.CreateDirectory(_directory);await using var connection=await OpenAsync(cancellationToken);
        await using var command=connection.CreateCommand();command.CommandText="""
            CREATE TABLE IF NOT EXISTS duplicate_hash_cache (
                path TEXT NOT NULL PRIMARY KEY,size_bytes INTEGER NOT NULL,file_modified_ticks INTEGER NOT NULL,
                sha256 TEXT NOT NULL,hashed_at_utc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_duplicate_hash_cache_sha256 ON duplicate_hash_cache(size_bytes,sha256);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    },cancellationToken),cancellationToken);

    public Task<SavedDuplicateHash?> TryGetAsync(string path,long size,DateTime? modified,CancellationToken token=default)=>Task.Run(async()=>
    {
        if(modified is null)return null;
        await using var connection=await OpenAsync(token);await using var command=connection.CreateCommand();
        command.CommandText="SELECT path,size_bytes,file_modified_ticks,sha256 FROM duplicate_hash_cache WHERE path=$path AND size_bytes=$size AND file_modified_ticks=$modified;";
        command.Parameters.AddWithValue("$path",path);command.Parameters.AddWithValue("$size",size);command.Parameters.AddWithValue("$modified",modified.Value.Ticks);
        await using var reader=await command.ExecuteReaderAsync(token);return await reader.ReadAsync(token) ? Read(reader) : null;
    },token);

    public Task<IReadOnlyList<SavedDuplicateHash>> LoadAsync(CancellationToken cancellationToken=default)=>Task.Run<IReadOnlyList<SavedDuplicateHash>>(async()=>
    {
        await using var connection=await OpenAsync(cancellationToken);await using var command=connection.CreateCommand();
        command.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE name='desktop_media_items');";
        var hasCatalog=Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken))==1;
        command.CommandText="SELECT path,size_bytes,file_modified_ticks,sha256 FROM duplicate_hash_cache h"+
            (hasCatalog ? " WHERE NOT EXISTS(SELECT 1 FROM desktop_media_items i WHERE i.path=h.path AND i.is_quarantined=1)" : "")+";";
        var result=new List<SavedDuplicateHash>();await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken))result.Add(Read(reader));return result;
    },cancellationToken);

    public Task SaveAsync(IEnumerable<SavedDuplicateHash> hashes,CancellationToken cancellationToken=default)=>Task.Run(async()=>
    {
        foreach(var batch in hashes.Chunk(128))await CatalogDatabaseAccess.WriteAsync(_directory,async()=>
        {
            await using var connection=await OpenAsync(cancellationToken);await using var transaction=connection.BeginTransaction();
            await using var command=connection.CreateCommand();command.Transaction=transaction;
            command.CommandText="""
                INSERT INTO duplicate_hash_cache(path,size_bytes,file_modified_ticks,sha256,hashed_at_utc)
                VALUES($path,$size,$modified,$hash,strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                ON CONFLICT(path) DO UPDATE SET size_bytes=excluded.size_bytes,file_modified_ticks=excluded.file_modified_ticks,
                    sha256=excluded.sha256,hashed_at_utc=excluded.hashed_at_utc;
                """;
            foreach(var hash in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(hash.Path)||string.IsNullOrWhiteSpace(hash.Hash))continue;
                command.Parameters.Clear();command.Parameters.AddWithValue("$path",hash.Path);command.Parameters.AddWithValue("$size",hash.SizeBytes);
                command.Parameters.AddWithValue("$modified",hash.FileModifiedAt?.Ticks??0);command.Parameters.AddWithValue("$hash",hash.Hash);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        },cancellationToken);
    },cancellationToken);
    private static SavedDuplicateHash Read(SqliteDataReader reader)=>new()
    {
        Path=reader.GetString(0),SizeBytes=reader.GetInt64(1),
        FileModifiedAt=reader.GetInt64(2)<=0 ? null : new DateTime(reader.GetInt64(2),DateTimeKind.Local),Hash=reader.GetString(3)
    };
    private Task<SqliteConnection> OpenAsync(CancellationToken token)=>CatalogDatabaseAccess.OpenAsync(_connectionString,token);
}
