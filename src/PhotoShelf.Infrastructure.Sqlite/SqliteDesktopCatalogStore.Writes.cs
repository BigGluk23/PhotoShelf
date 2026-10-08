using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Metadata;

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
                await using var connection=await CatalogDatabaseAccess.OpenAsync(_connectionString, token, useWriteCache: true);
                await using var transaction=connection.BeginTransaction();
                var caches = new List<string>();
                foreach (var table in new[] { "desktop_metadata_cache", "duplicate_hash_cache" })
                    if (await TableExistsAsync(connection, transaction, table, token)) caches.Add(table);
                await using var command=connection.CreateCommand();command.Transaction=transaction;
                command.CommandText="""
                    INSERT INTO desktop_media_items(asset_id,path,path_key,folder_key,search_key,is_favorite,size_bytes,
                        file_modified_utc_ticks,file_local_ticks,file_month,is_video,capture_date_ticks,capture_month,metadata_indexed,metadata_status,metadata_attempted_ticks,metadata_retry_ticks,metadata_error_code,is_hidden_or_system,last_seen_utc,availability,availability_checked_ticks,availability_error_code,file_identity)
                    VALUES($id,$path,$pathKey,$folder,$pathKey,$favorite,$size,$modified,$local,$fileMonth,$video,$capture,$captureMonth,$indexed,$status,$attempted,$retry,$error,$system,strftime('%Y-%m-%dT%H:%M:%fZ','now'),$availability,$checked,$availabilityError,$identity)
                    ON CONFLICT(path_key) DO UPDATE SET
                        path=excluded.path,folder_key=excluded.folder_key,search_key=excluded.search_key,
                        is_favorite=CASE WHEN $preserveFavorite=1 THEN desktop_media_items.is_favorite ELSE excluded.is_favorite END,
                        capture_date_ticks=CASE WHEN excluded.metadata_indexed=1 THEN excluded.capture_date_ticks
                            WHEN desktop_media_items.size_bytes=excluded.size_bytes AND desktop_media_items.file_modified_utc_ticks=excluded.file_modified_utc_ticks THEN desktop_media_items.capture_date_ticks ELSE NULL END,
                        capture_month=CASE WHEN excluded.metadata_indexed=1 THEN excluded.capture_month
                            WHEN desktop_media_items.size_bytes=excluded.size_bytes AND desktop_media_items.file_modified_utc_ticks=excluded.file_modified_utc_ticks THEN desktop_media_items.capture_month ELSE NULL END,
                        metadata_indexed=CASE WHEN excluded.metadata_indexed=1 THEN 1
                            WHEN desktop_media_items.size_bytes=excluded.size_bytes AND desktop_media_items.file_modified_utc_ticks=excluded.file_modified_utc_ticks THEN desktop_media_items.metadata_indexed ELSE 0 END,
                        metadata_status=CASE WHEN excluded.metadata_indexed=1 THEN excluded.metadata_status
                            WHEN desktop_media_items.size_bytes=excluded.size_bytes AND desktop_media_items.file_modified_utc_ticks=excluded.file_modified_utc_ticks THEN desktop_media_items.metadata_status ELSE 0 END,
                        metadata_attempted_ticks=CASE WHEN excluded.metadata_indexed=1 THEN excluded.metadata_attempted_ticks
                            WHEN desktop_media_items.size_bytes=excluded.size_bytes AND desktop_media_items.file_modified_utc_ticks=excluded.file_modified_utc_ticks THEN desktop_media_items.metadata_attempted_ticks ELSE NULL END,
                        metadata_retry_ticks=CASE WHEN excluded.metadata_indexed=1 THEN excluded.metadata_retry_ticks
                            WHEN desktop_media_items.size_bytes=excluded.size_bytes AND desktop_media_items.file_modified_utc_ticks=excluded.file_modified_utc_ticks THEN desktop_media_items.metadata_retry_ticks ELSE NULL END,
                        metadata_error_code=CASE WHEN excluded.metadata_indexed=1 THEN excluded.metadata_error_code
                            WHEN desktop_media_items.size_bytes=excluded.size_bytes AND desktop_media_items.file_modified_utc_ticks=excluded.file_modified_utc_ticks THEN desktop_media_items.metadata_error_code ELSE NULL END,
                        observation_version=desktop_media_items.observation_version+CASE WHEN
                            desktop_media_items.size_bytes<>excluded.size_bytes OR desktop_media_items.file_modified_utc_ticks<>excluded.file_modified_utc_ticks
                            OR excluded.availability_checked_ticks IS NOT NULL THEN 1 ELSE 0 END,
                        availability=CASE WHEN excluded.availability_checked_ticks IS NOT NULL THEN excluded.availability ELSE desktop_media_items.availability END,
                        availability_checked_ticks=COALESCE(excluded.availability_checked_ticks,desktop_media_items.availability_checked_ticks),
                        availability_error_code=CASE WHEN excluded.availability_checked_ticks IS NOT NULL THEN excluded.availability_error_code ELSE desktop_media_items.availability_error_code END,
                        file_identity=CASE WHEN excluded.file_identity IS NOT NULL THEN excluded.file_identity
                            WHEN desktop_media_items.size_bytes=excluded.size_bytes AND desktop_media_items.file_modified_utc_ticks=excluded.file_modified_utc_ticks THEN desktop_media_items.file_identity ELSE NULL END,
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
                    var status = item.MetadataStatus == MetadataReadStatus.Pending && item.MetadataIndexed
                        ? (item.CaptureDate is null ? MetadataReadStatus.Absent : MetadataReadStatus.Found) : item.MetadataStatus;
                    command.Parameters.AddWithValue("$status", (int)status);
                    command.Parameters.AddWithValue("$attempted", (object?)item.MetadataAttemptedAtUtc?.Ticks ?? DBNull.Value);
                    command.Parameters.AddWithValue("$retry", (object?)item.MetadataRetryAtUtc?.Ticks ?? DBNull.Value);
                    command.Parameters.AddWithValue("$error", (object?)item.MetadataErrorCode ?? DBNull.Value);
                    command.Parameters.AddWithValue("$preserveFavorite",preserveFavorites ? 1 : 0);
                    command.Parameters.AddWithValue("$availability", (int)item.Availability);
                    command.Parameters.AddWithValue("$checked", (object?)item.AvailabilityCheckedAtUtc?.ToUniversalTime().Ticks ?? DBNull.Value);
                    command.Parameters.AddWithValue("$availabilityError", (object?)item.AvailabilityErrorCode ?? DBNull.Value);
                    command.Parameters.AddWithValue("$identity", (object?)item.FileIdentity ?? DBNull.Value);
                    foreach (var table in caches)
                    {
                        await using var invalidate = connection.CreateCommand(); invalidate.Transaction = transaction;
                        invalidate.CommandText = $"DELETE FROM {table} WHERE path IN (SELECT path FROM desktop_media_items WHERE path_key=$key AND (size_bytes<>$size OR file_modified_utc_ticks<>$modified));";
                        invalidate.Parameters.AddWithValue("$key", NormalizePathKey(item.Path));
                        invalidate.Parameters.AddWithValue("$size", item.SizeBytes);
                        invalidate.Parameters.AddWithValue("$modified", item.FileModifiedAt?.ToUniversalTime().Ticks ?? 0);
                        await invalidate.ExecuteNonQueryAsync(token);
                    }
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

    public Task UpdateCaptureDateAsync(string path,long size,DateTime? modified,DateTime? captureDate,CancellationToken token=default) =>
        UpdateMetadataResultAsync(path,size,modified,new(captureDate is null ? MetadataReadStatus.Absent : MetadataReadStatus.Found,captureDate),DateTime.UtcNow,token);

    public Task<bool> UpdateMetadataResultAsync(string path,long size,DateTime? modified,CaptureDateReadResult result,DateTime attemptedUtc,CancellationToken token=default, long? expectedObservationVersion=null) => Task.Run(async () =>
    {
        var updated = false;
        await CatalogDatabaseAccess.WriteAsync(_directory,async () =>
        {
            await using var connection=await OpenAsync(token);await using var command=connection.CreateCommand();
            command.CommandText="""
                UPDATE desktop_media_items SET
                    capture_date_ticks=CASE WHEN $authoritative=1 THEN $capture ELSE capture_date_ticks END,
                    capture_month=CASE WHEN $authoritative=1 THEN $month ELSE capture_month END,
                    metadata_indexed=$terminal,metadata_status=$status,metadata_attempted_ticks=$attempted,
                    metadata_retry_ticks=$retry,metadata_error_code=$error
                WHERE path_key=$path AND size_bytes=$size AND file_modified_utc_ticks=$modified AND is_quarantined=0
                    AND ($version IS NULL OR observation_version=$version) AND (availability=0 OR (availability=4 AND availability_error_code IS NULL));
                """;
            command.Parameters.AddWithValue("$version", (object?)expectedObservationVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("$authoritative",result.IsAuthoritative?1:0);
            command.Parameters.AddWithValue("$capture",(object?)result.CaptureDate?.Ticks??DBNull.Value);
            command.Parameters.AddWithValue("$month",(object?)Month(result.CaptureDate)??DBNull.Value);
            command.Parameters.AddWithValue("$terminal",result.IsTerminal?1:0);
            command.Parameters.AddWithValue("$status",(int)result.Status);
            command.Parameters.AddWithValue("$attempted",attemptedUtc.ToUniversalTime().Ticks);
            command.Parameters.AddWithValue("$retry",(object?)result.RetryAtUtc(attemptedUtc.ToUniversalTime())?.Ticks??DBNull.Value);
            command.Parameters.AddWithValue("$error",(object?)result.ErrorCode??DBNull.Value);
            command.Parameters.AddWithValue("$path",NormalizePathKey(path));
            command.Parameters.AddWithValue("$size",size);command.Parameters.AddWithValue("$modified",modified?.ToUniversalTime().Ticks??0);
            updated=await command.ExecuteNonQueryAsync(token)==1;
        },token);
        return updated;
    },token);

    public Task ResetMetadataIndexAsync(CancellationToken token=default)=>Task.Run(()=>CatalogDatabaseAccess.WriteAsync(_directory,async()=>
    {
        await using var connection=await OpenAsync(token);
        // Retain previous dates while a cancellable reindex is in progress.
        await ExecuteAsync(connection,null,"UPDATE desktop_media_items SET metadata_indexed=0,metadata_retry_ticks=NULL WHERE is_quarantined=0;",token);
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
            command.CommandText="UPDATE desktop_media_items SET path=$destination,path_key=$key,folder_key=$folder,search_key=$key,is_quarantined=$quarantine,file_identity=NULL,availability=4,availability_checked_ticks=NULL,availability_error_code=NULL,observation_version=observation_version+1 WHERE asset_id=$id;";
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
