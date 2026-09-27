using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;

namespace PhotoShelf.Infrastructure.Sqlite;

public sealed partial class SqliteDesktopCatalogStore
{
    /// <summary>
    /// Resolve both endpoints and read the inclusive Shift-selection range from one
    /// SQLite snapshot. Only explicitly selected records are materialized. Scanning
    /// may continue writing without shifting offsets between selection batches.
    /// </summary>
    public Task<IReadOnlyList<SavedMediaItem>> QueryRangeAsync(CatalogViewQuery query, string anchorPath, string targetPath,
        CancellationToken token = default) => Task.Run<IReadOnlyList<SavedMediaItem>>(async () =>
    {
        await using var connection = await OpenAsync(token);
        using var interrupt = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        await using var transaction = connection.BeginTransaction(deferred: true);
        var date = SortExpression(query);
        async Task<(long Ticks, string Key)?> EndpointAsync(string path)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"SELECT {date},path_key FROM {BuildFrom(query, command)} WHERE {BuildGroupFilter(query, command)} AND path_key=$endpoint;";
            command.Parameters.AddWithValue("$endpoint", NormalizePathKey(path));
            await using var reader = await command.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token) ? (reader.GetInt64(0), reader.GetString(1)) : null;
        }
        try
        {
            var anchor = await EndpointAsync(anchorPath);
            var target = await EndpointAsync(targetPath);
            if (anchor is null || target is null) return Array.Empty<SavedMediaItem>();
            var first = anchor.Value; var last = target.Value;
            var comparison = first.Ticks.CompareTo(last.Ticks) * (query.NewestFirst ? -1 : 1);
            var reverseTie = false;
            if (comparison == 0)
            {
                // Let SQLite apply exactly the same path collation as the page query,
                // including non-BMP Unicode names whose UTF-16 ordinal order differs.
                await using var order = connection.CreateCommand(); order.Transaction = transaction;
                order.CommandText = "SELECT $first COLLATE BINARY > $last COLLATE BINARY;";
                order.Parameters.AddWithValue("$first", first.Key); order.Parameters.AddWithValue("$last", last.Key);
                reverseTie = Convert.ToInt64(await order.ExecuteScalarAsync(token)) != 0;
            }
            if (comparison > 0 || reverseTie)
                (first, last) = (last, first);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            var where = BuildGroupFilter(query, command); var from = BuildFrom(query, command);
            command.CommandText = $"""
                SELECT {ItemColumns} FROM {from} WHERE {where}
                AND ({date} {(query.NewestFirst ? "<" : ">")} $firstTicks OR ({date}=$firstTicks AND path_key>=$firstKey))
                AND ({date} {(query.NewestFirst ? ">" : "<")} $lastTicks OR ({date}=$lastTicks AND path_key<=$lastKey))
                ORDER BY {date} {(query.NewestFirst ? "DESC" : "ASC")},path_key ASC;
                """;
            command.Parameters.AddWithValue("$firstTicks", first.Ticks); command.Parameters.AddWithValue("$firstKey", first.Key);
            command.Parameters.AddWithValue("$lastTicks", last.Ticks); command.Parameters.AddWithValue("$lastKey", last.Key);
            var selected = new List<SavedMediaItem>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) { token.ThrowIfCancellationRequested(); selected.Add(ReadItem(reader)); }
            return selected;
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }, token);

    public Task<CatalogPage> QueryPageAsync(CatalogViewQuery query, int offset, int limit, string? groupKey=null, CancellationToken token=default)
        => QueryPageAsync(query with { Offset=offset, PageSize=limit, GroupKey=groupKey, Cursor=null },token);

    public Task<CatalogPage> QueryPageAsync(CatalogViewQuery query, CancellationToken token=default) => Task.Run(async () =>
    {
        if (query.PageSize is <1 or >1024 || query.Offset<0) throw new ArgumentOutOfRangeException(nameof(query));
        token.ThrowIfCancellationRequested();
        await using var connection=await OpenAsync(token);
        using var interrupt=token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        await using var command=connection.CreateCommand();
        var from=BuildFrom(query,command); var where=BuildGroupFilter(query,command);
        var date=SortExpression(query);
        if(query.Cursor is not null)
        {
            var cursor=JsonSerializer.Deserialize<PageCursor>(Convert.FromBase64String(query.Cursor)) ?? throw new ArgumentException("Invalid page cursor.");
            if(cursor.View!=Fingerprint(query)) throw new ArgumentException("Page cursor belongs to a different view.");
            where += $" AND ({date} {(query.NewestFirst ? "<" : ">")} $cursorDate OR ({date}=$cursorDate AND path_key>$cursorPath))";
            command.Parameters.AddWithValue("$cursorDate",cursor.Ticks);command.Parameters.AddWithValue("$cursorPath",cursor.PathKey);
        }
        command.CommandText=$"SELECT {ItemColumns} FROM {from} WHERE {where} ORDER BY {date} {(query.NewestFirst ? "DESC" : "ASC")},path_key ASC LIMIT $take OFFSET $skip;";
        command.Parameters.AddWithValue("$take",query.PageSize+1);command.Parameters.AddWithValue("$skip",query.Cursor is null ? query.Offset : 0);
        var result=new List<SavedMediaItem>(query.PageSize+1);
        long lastDateTicks=0;
        try
        {
            await using var reader=await command.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))
            {
                token.ThrowIfCancellationRequested();result.Add(ReadItem(reader));
                if(result.Count<=query.PageSize)lastDateTicks=query.UseCaptureDate ? (reader.IsDBNull(6) ? 0 : reader.GetInt64(6)) : reader.GetInt64(9);
            }
        }
        catch(SqliteException) when(token.IsCancellationRequested) {throw new OperationCanceledException(token);}
        var hasMore=result.Count>query.PageSize;
        if(hasMore) result.RemoveAt(result.Count-1);
        var last=result.LastOrDefault();
        var next=hasMore && last is not null ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new PageCursor(
            lastDateTicks,NormalizePathKey(last.Path),Fingerprint(query)))) : null;
        return new CatalogPage(result,next,hasMore);
    },token);

    public Task<long> CountAsync(CatalogViewQuery query, CancellationToken token=default) => Task.Run(async () =>
    {
        await using var connection=await OpenAsync(token);
        using var interrupt=token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        await using var command=connection.CreateCommand();
        command.CommandText=$"SELECT COUNT(*) FROM {BuildFrom(query,command)} WHERE {BuildGroupFilter(query,command)};";
        try {return Convert.ToInt64(await command.ExecuteScalarAsync(token));}
        catch(SqliteException) when(token.IsCancellationRequested) {throw new OperationCanceledException(token);}
    },token);

    public Task<IReadOnlyList<CatalogDateGroup>> QueryGroupsAsync(CatalogViewQuery query, CancellationToken token=default) => Task.Run<IReadOnlyList<CatalogDateGroup>>(async () =>
    {
        await using var connection=await OpenAsync(token);
        using var interrupt=token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        await using var command=connection.CreateCommand();
        var month=query.UseCaptureDate ? "capture_month" : "file_month";
        command.CommandText=$"SELECT {month},COUNT(*) FROM {BuildFrom(query,command)} WHERE {BuildGroupFilter(query,command)} GROUP BY {month} ORDER BY {month} {(query.NewestFirst ? "DESC" : "ASC")};";
        var result=new List<CatalogDateGroup>();
        try
        {
            await using var reader=await command.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))
            {
                token.ThrowIfCancellationRequested(); var value=reader.IsDBNull(0) ? null : reader.GetString(0);
                result.Add(new CatalogDateGroup((query.UseCaptureDate ? "capture:" : "file:")+(value??"none"),
                    value is null ? null : int.Parse(value[..4],CultureInfo.InvariantCulture),
                    value is null ? null : int.Parse(value[5..],CultureInfo.InvariantCulture),reader.GetInt64(1)));
            }
        }
        catch(SqliteException) when(token.IsCancellationRequested) {throw new OperationCanceledException(token);}
        return result;
    },token);

    public async IAsyncEnumerable<SavedMediaItem> EnumerateAsync(CatalogViewQuery query, [EnumeratorCancellation] CancellationToken token=default)
    {
        query=query with {Offset=0,Cursor=null};
        do
        {
            token.ThrowIfCancellationRequested(); var page=await QueryPageAsync(query,token).ConfigureAwait(false);
            foreach(var item in page.Items) {token.ThrowIfCancellationRequested(); yield return item;}
            if(!page.HasMore) yield break;
            query=query with {Cursor=page.NextCursor};
        } while(true);
    }

    public Task<long?> IndexOfAsync(CatalogViewQuery query,string path,CancellationToken token=default)=>Task.Run<long?>(async()=>
    {
        await using var connection=await OpenAsync(token);
        using var interrupt=token.Register(()=>SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        await using var transaction=connection.BeginTransaction(deferred:true);
        long ticks;
        try
        {
            await using(var target=connection.CreateCommand())
            {
                target.Transaction=transaction;
                target.CommandText=$"SELECT {SortExpression(query)} FROM {BuildFrom(query,target)} WHERE {BuildGroupFilter(query,target)} AND path_key=$target;";
                target.Parameters.AddWithValue("$target",NormalizePathKey(path));var value=await target.ExecuteScalarAsync(token);
                if(value is null)return null;ticks=Convert.ToInt64(value);
            }
            await using var command=connection.CreateCommand();command.Transaction=transaction;
            var date=SortExpression(query);
            command.CommandText=$"SELECT COUNT(*) FROM {BuildFrom(query,command)} WHERE {BuildGroupFilter(query,command)} AND ({date} {(query.NewestFirst ? ">" : "<")} $ticks OR ({date}=$ticks AND path_key<$target));";
            command.Parameters.AddWithValue("$ticks",ticks);command.Parameters.AddWithValue("$target",NormalizePathKey(path));
            return Convert.ToInt64(await command.ExecuteScalarAsync(token));
        }
        catch(SqliteException) when(token.IsCancellationRequested){throw new OperationCanceledException(token);}
    },token);

    public Task<SavedMediaItem?> GetItemAsync(string path, CancellationToken token=default) => Task.Run(async () =>
    {
        await using var connection=await OpenAsync(token); await using var command=connection.CreateCommand();
        command.CommandText=$"SELECT {ItemColumns} FROM desktop_media_items WHERE path_key=$path;";
        command.Parameters.AddWithValue("$path",NormalizePathKey(path)); await using var reader=await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadItem(reader) : null;
    },token);

    public Task<IReadOnlyList<string>> GetImmediateFoldersAsync(string parent,CancellationToken token=default)=>Task.Run<IReadOnlyList<string>>(async()=>
    {
        await using var connection=await OpenAsync(token);
        using var interrupt=token.Register(()=>SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        await using var command=connection.CreateCommand();
        var prefix=NormalizePathKey(parent)+"/";
        // Group by normalized immediate child, preserve one original path's display casing.
        command.CommandText="""
            SELECT MIN(substr(path,1,length($prefix)+instr(substr(folder_key||'/',length($prefix)+1),'/')-1))
            FROM desktop_media_items WHERE is_quarantined=0 AND folder_key>=$prefix AND folder_key<$end
            GROUP BY substr(folder_key,1,length($prefix)+instr(substr(folder_key||'/',length($prefix)+1),'/')-1)
            ORDER BY folder_key;
            """;
        command.Parameters.AddWithValue("$prefix",prefix);command.Parameters.AddWithValue("$end",NormalizePathKey(parent)+"0");
        var result=new List<string>();
        try
        {
            await using var reader=await command.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token)){token.ThrowIfCancellationRequested();result.Add(reader.GetString(0));}
        }
        catch(SqliteException) when(token.IsCancellationRequested){throw new OperationCanceledException(token);}
        return result;
    },token);

    private static string BuildFrom(CatalogViewQuery query,SqliteCommand command)
    {
        var where=new List<string>{"is_quarantined=0"};
        if(query.MetadataDueAtUtc is { } due)
        {
            where.Add("metadata_indexed=0 AND is_video=0 AND (metadata_retry_ticks IS NULL OR metadata_retry_ticks<=$metadataDue)");
            command.Parameters.AddWithValue("$metadataDue", due.ToUniversalTime().Ticks);
        }
        if(!query.ShowVideos)where.Add("is_video=0");
        if(!query.IncludeSystemFolders)where.Add("is_hidden_or_system=0");
        if(query.ViewMode=="Favorites")where.Add("is_favorite=1");
        if(query.MissingCaptureDateOnly)where.Add("capture_date_ticks IS NULL AND is_video=0");
        if(query.DuplicateCandidatesOnly)where.Add("size_bytes IN (SELECT size_bytes FROM desktop_size_counts WHERE item_count>1)");
        if(!string.IsNullOrWhiteSpace(query.SearchText))
        {
            where.Add("instr(search_key,$text)>0");command.Parameters.AddWithValue("$text",query.SearchText.Replace('\\','/').ToUpperInvariant());
        }
        if(!string.IsNullOrWhiteSpace(query.Folder))
        {
            var folder=NormalizePathKey(query.Folder);
            if(query.IncludeSubfolders)
            {
                where.Add("path_key>=$folderStart AND path_key<$folderEnd");
                command.Parameters.AddWithValue("$folderStart",folder+"/");command.Parameters.AddWithValue("$folderEnd",folder+"0");
            }
            else {where.Add("folder_key=$folder");command.Parameters.AddWithValue("$folder",folder);}
        }
        // Explorer folder views intentionally show that folder even when not included in the library.
        if(query.ViewMode!="Folder")
        {
            for(var i=0;i<query.ExcludedFolders.Count;i++)
            {
                var folder=NormalizePathKey(query.ExcludedFolders[i]);
                where.Add($"NOT(path_key>=$excludedStart{i} AND path_key<$excludedEnd{i})");
                command.Parameters.AddWithValue($"$excludedStart{i}",folder+"/");command.Parameters.AddWithValue($"$excludedEnd{i}",folder+"0");
            }
        }
        var filtered=$"(SELECT * FROM desktop_media_items WHERE {string.Join(" AND ",where)}";
        if(query.ViewMode=="Recent")filtered+=" ORDER BY file_modified_utc_ticks DESC,path_key ASC LIMIT 500";
        return filtered+") AS items";
    }

    private static string BuildGroupFilter(CatalogViewQuery query, SqliteCommand command)
    {
        if(query.GroupKey is null)return "1=1";
        var prefix=query.UseCaptureDate ? "capture:" : "file:";
        if(!query.GroupKey.StartsWith(prefix,StringComparison.Ordinal))throw new ArgumentException("Group key belongs to another date mode.");
        var month=query.UseCaptureDate ? "capture_month" : "file_month";var value=query.GroupKey[prefix.Length..];
        if(value=="none")return month+" IS NULL";
        if(!DateTime.TryParseExact(value,"yyyy-MM",CultureInfo.InvariantCulture,DateTimeStyles.None,out _))throw new ArgumentException("Invalid date group.");
        command.Parameters.AddWithValue("$month",value);return month+"=$month";
    }
    private static string SortExpression(CatalogViewQuery query)=>query.UseCaptureDate ? "COALESCE(capture_date_ticks,0)" : "file_local_ticks";
    private static string Fingerprint(CatalogViewQuery query)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(query with {Cursor=null,Offset=0,PageSize=0})))[..24];
    private sealed record PageCursor(long Ticks,string PathKey,string View);
    private const string ItemColumns="asset_id,path,is_favorite,size_bytes,file_modified_utc_ticks,is_video,capture_date_ticks,metadata_indexed,is_hidden_or_system,file_local_ticks,metadata_status,metadata_attempted_ticks,metadata_retry_ticks,metadata_error_code";
    private static SavedMediaItem ReadItem(SqliteDataReader r)=>new()
    {
        AssetId=r.GetString(0),Path=r.GetString(1),IsFavorite=r.GetInt64(2)==1,SizeBytes=r.GetInt64(3),
        FileModifiedAt=r.GetInt64(4)==0 ? null : new DateTime(r.GetInt64(4),DateTimeKind.Utc).ToLocalTime(),
        IsVideo=r.GetInt64(5)==1,CaptureDate=r.IsDBNull(6) ? null : new DateTime(r.GetInt64(6),DateTimeKind.Unspecified),
        MetadataIndexed=r.GetInt64(7)==1,IsHiddenOrSystem=r.GetInt64(8)==1,
        MetadataStatus=(PhotoShelf.Application.Metadata.MetadataReadStatus)r.GetInt32(10),
        MetadataAttemptedAtUtc=r.IsDBNull(11)?null:new DateTime(r.GetInt64(11),DateTimeKind.Utc),
        MetadataRetryAtUtc=r.IsDBNull(12)?null:new DateTime(r.GetInt64(12),DateTimeKind.Utc),
        MetadataErrorCode=r.IsDBNull(13)?null:r.GetString(13)
    };
}
