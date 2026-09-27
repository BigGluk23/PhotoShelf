using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;

namespace PhotoShelf.Infrastructure.Sqlite;

public sealed partial class SqliteDesktopCatalogStore
{
    public Task UpsertItemsAsync(IEnumerable<SavedMediaItem> items, bool preserveFavorites=true, CancellationToken token=default) => Task.Run(async () =>
    {
        // Commit bounded batches so scan work yields to interactive favorites/moves.
        foreach(var batch in items.Chunk(256))
        {
            token.ThrowIfCancellationRequested();
            await CatalogDatabaseAccess.WriteAsync(_directory, async () =>
            {
                await using var connection=await OpenAsync(token);await using var transaction=connection.BeginTransaction();
                await using var command=connection.CreateCommand();command.Transaction=transaction;
                command.CommandText="""
                    INSERT INTO desktop_media_items(asset_id,path,path_key,folder_key,search_key,is_favorite,size_bytes,
                        file_modified_utc_ticks,file_local_ticks,file_month,is_video,capture_date_ticks,capture_month,metadata_indexed,is_hidden_or_system,last_seen_utc)
                    VALUES($id,$path,$pathKey,$folder,$pathKey,$favorite,$size,$modified,$local,$fileMonth,$video,$capture,$captureMonth,$indexed,$system,strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                    ON CONFLICT(path_key) DO UPDATE SET
                        path=excluded.path,folder_key=excluded.folder_key,search_key=excluded.search_key,
                        is_favorite=CASE WHEN $preserveFavorite=1 THEN desktop_media_items.is_favorite ELSE excluded.is_favorite END,
                        capture_date_ticks=CASE WHEN excluded.metadata_indexed=1 THEN excluded.capture_date_ticks
                            WHEN desktop_media_items.size_bytes=excluded.size_bytes AND desktop_media_items.file_modified_utc_ticks=excluded.file_modified_utc_ticks THEN desktop_media_items.capture_date_ticks ELSE NULL END,
                        capture_month=CASE WHEN excluded.metadata_indexed=1 THEN excluded.capture_month
                            WHEN desktop_media_items.size_bytes=excluded.size_bytes AND desktop_media_items.file_modified_utc_ticks=excluded.file_modified_utc_ticks THEN desktop_media_items.capture_month ELSE NULL END,
                        metadata_indexed=CASE WHEN excluded.metadata_indexed=1 THEN 1
                            WHEN desktop_media_items.size_bytes=excluded.size_bytes AND desktop_media_items.file_modified_utc_ticks=excluded.file_modified_utc_ticks THEN desktop_media_items.metadata_indexed ELSE 0 END,
                        size_bytes=excluded.size_bytes,file_modified_utc_ticks=excluded.file_modified_utc_ticks,
                        file_local_ticks=excluded.file_local_ticks,file_month=excluded.file_month,
                        is_video=excluded.is_video,is_hidden_or_system=excluded.is_hidden_or_system,last_seen_utc=excluded.last_seen_utc;
                    """;
                foreach(var item in batch)
                {
                    token.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(item.Path))continue;
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("$id",string.IsNullOrEmpty(item.AssetId) ? Guid.NewGuid().ToString("N") : item.AssetId);
                    command.Parameters.AddWithValue("$path",item.Path);command.Parameters.AddWithValue("$pathKey",NormalizePathKey(item.Path));
                    command.Parameters.AddWithValue("$folder",FolderKey(item.Path));command.Parameters.AddWithValue("$favorite",item.IsFavorite ? 1 : 0);
                    command.Parameters.AddWithValue("$size",item.SizeBytes);command.Parameters.AddWithValue("$modified",item.FileModifiedAt?.ToUniversalTime().Ticks??0);
                    var local=item.FileModifiedAt is {Kind:DateTimeKind.Utc} utc ? utc.ToLocalTime() : item.FileModifiedAt;
                    command.Parameters.AddWithValue("$local",local?.Ticks??0);command.Parameters.AddWithValue("$fileMonth",(object?)Month(local)??DBNull.Value);
                    command.Parameters.AddWithValue("$video",item.IsVideo ? 1 : 0);command.Parameters.AddWithValue("$capture",(object?)item.CaptureDate?.Ticks??DBNull.Value);
                    command.Parameters.AddWithValue("$captureMonth",(object?)Month(item.CaptureDate)??DBNull.Value);
                    command.Parameters.AddWithValue("$indexed",item.MetadataIndexed ? 1 : 0);command.Parameters.AddWithValue("$system",item.IsHiddenOrSystem ? 1 : 0);
                    command.Parameters.AddWithValue("$preserveFavorite",preserveFavorites ? 1 : 0);
                    await command.ExecuteNonQueryAsync(token);
                }
                await transaction.CommitAsync(token);
            },token);
        }
    },token);

    public Task SetFavoriteAsync(string path,bool favorite,CancellationToken token=default)=>Task.Run(()=>CatalogDatabaseAccess.WriteAsync(_directory,async()=>
    {
        await using var connection=await OpenAsync(token);await using var command=connection.CreateCommand();
        command.CommandText="UPDATE desktop_media_items SET is_favorite=$favorite WHERE path_key=$path;";
        command.Parameters.AddWithValue("$favorite",favorite ? 1 : 0);command.Parameters.AddWithValue("$path",NormalizePathKey(path));
        await command.ExecuteNonQueryAsync(token);
    },token),token);

    public Task UpdateCaptureDateAsync(string path,long size,DateTime? modified,DateTime? captureDate,CancellationToken token=default)=>Task.Run(()=>CatalogDatabaseAccess.WriteAsync(_directory,async()=>
    {
        await using var connection=await OpenAsync(token);await using var command=connection.CreateCommand();
        command.CommandText="""
            UPDATE desktop_media_items SET capture_date_ticks=$capture,capture_month=$month,metadata_indexed=1
            WHERE path_key=$path AND size_bytes=$size AND file_modified_utc_ticks=$modified AND is_quarantined=0;
            """;
        command.Parameters.AddWithValue("$capture",(object?)captureDate?.Ticks??DBNull.Value);
        command.Parameters.AddWithValue("$month",(object?)Month(captureDate)??DBNull.Value);command.Parameters.AddWithValue("$path",NormalizePathKey(path));
        command.Parameters.AddWithValue("$size",size);command.Parameters.AddWithValue("$modified",modified?.ToUniversalTime().Ticks??0);
        await command.ExecuteNonQueryAsync(token);
    },token),token);

    public Task ResetMetadataIndexAsync(CancellationToken token=default)=>Task.Run(()=>CatalogDatabaseAccess.WriteAsync(_directory,async()=>
    {
        await using var connection=await OpenAsync(token);
        // Retain previous dates while a cancellable reindex is in progress.
        await ExecuteAsync(connection,null,"UPDATE desktop_media_items SET metadata_indexed=0 WHERE is_quarantined=0;",token);
    },token),token);

    /// <summary>Remove from displayed library only; retain identity and user data for recovery.</summary>
    public Task RemoveItemsAsync(IEnumerable<string> paths,CancellationToken token=default)=>Task.Run(async()=>
    {
        foreach(var batch in paths.Chunk(256))await CatalogDatabaseAccess.WriteAsync(_directory,async()=>
        {
            await using var connection=await OpenAsync(token);await using var transaction=connection.BeginTransaction();
            foreach(var path in batch)
            {
                await using var command=connection.CreateCommand();command.Transaction=transaction;
                command.CommandText="UPDATE desktop_media_items SET is_quarantined=1 WHERE path_key=$path;";
                command.Parameters.AddWithValue("$path",NormalizePathKey(path));await command.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
        },token);
    },token);

    /// <summary>
    /// Catalog half of a journaled file operation. Never overwrites an existing catalog identity.
    /// Repeating a committed callback is accepted only when its durable receipt matches the destination.
    /// Quarantine retains the row and metadata; reverse MoveItemAsync restores it.
    /// </summary>
    public Task MoveItemAsync(string source,string destination,bool removeFromLibrary=false)=>Task.Run(()=>CatalogDatabaseAccess.WriteAsync(_directory,async()=>
    {
        await using var connection=await OpenAsync(CancellationToken.None);await using var transaction=connection.BeginTransaction();
        var sourceKey=NormalizePathKey(source);var destinationKey=NormalizePathKey(destination);
        async Task<(string? Id,string? Path)> Find(string key)
        {
            await using var command=connection.CreateCommand();command.Transaction=transaction;
            command.CommandText="SELECT asset_id,path FROM desktop_media_items WHERE path_key=$key;";command.Parameters.AddWithValue("$key",key);
            await using var reader=await command.ExecuteReaderAsync();return await reader.ReadAsync() ? (reader.GetString(0),reader.GetString(1)) : (null,null);
        }
        var from=await Find(sourceKey);var to=await Find(destinationKey);
        if(from.Id is null && to.Id is not null)
        {
            await using var receipt=connection.CreateCommand();receipt.Transaction=transaction;
            receipt.CommandText="SELECT asset_id FROM desktop_move_receipts WHERE source_key=$source AND destination_key=$destination AND quarantined=$quarantine;";
            receipt.Parameters.AddWithValue("$source",sourceKey);receipt.Parameters.AddWithValue("$destination",destinationKey);receipt.Parameters.AddWithValue("$quarantine",removeFromLibrary?1:0);
            if(!Equals(await receipt.ExecuteScalarAsync(),to.Id))throw new IOException("Destination belongs to another catalog item; move reconciliation was stopped.");
            await transaction.CommitAsync();return;
        }
        if(from.Id is not null && to.Id is not null && from.Id!=to.Id)throw new IOException("Destination already exists in the catalog; no metadata was overwritten.");
        if(from.Id is not null)
        {
            await using var command=connection.CreateCommand();command.Transaction=transaction;
            command.CommandText="UPDATE desktop_media_items SET path=$destination,path_key=$key,folder_key=$folder,search_key=$key,is_quarantined=$quarantine WHERE asset_id=$id;";
            command.Parameters.AddWithValue("$destination",destination);command.Parameters.AddWithValue("$key",destinationKey);
            command.Parameters.AddWithValue("$folder",FolderKey(destination));command.Parameters.AddWithValue("$quarantine",removeFromLibrary?1:0);command.Parameters.AddWithValue("$id",from.Id);
            await command.ExecuteNonQueryAsync();
        }
        foreach(var table in new[]{"desktop_metadata_cache","duplicate_hash_cache"})
        {
            if(!await TableExistsAsync(connection,transaction,table,CancellationToken.None))continue;
            await using var command=connection.CreateCommand();command.Transaction=transaction;
            // A cache collision fails the transaction rather than replacing another file's metadata.
            command.CommandText=$"UPDATE {table} SET path=$destination WHERE path=$source;";
            command.Parameters.AddWithValue("$destination",destination);command.Parameters.AddWithValue("$source",from.Path??source);await command.ExecuteNonQueryAsync();
        }
        await using(var receipt=connection.CreateCommand())
        {
            receipt.Transaction=transaction;receipt.CommandText="""
                INSERT INTO desktop_move_receipts(source_key,destination_key,quarantined,asset_id,committed_at_utc)
                VALUES($source,$destination,$quarantine,$id,strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                ON CONFLICT(source_key,destination_key,quarantined) DO UPDATE SET asset_id=excluded.asset_id,committed_at_utc=excluded.committed_at_utc;
                """;
            receipt.Parameters.AddWithValue("$source",sourceKey);receipt.Parameters.AddWithValue("$destination",destinationKey);
            receipt.Parameters.AddWithValue("$quarantine",removeFromLibrary?1:0);receipt.Parameters.AddWithValue("$id",(object?)from.Id??DBNull.Value);await receipt.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    },CancellationToken.None));
}
