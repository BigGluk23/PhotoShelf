using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class CatalogProjectionIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-projection-index-" + Guid.NewGuid().ToString("N"));
    private const string FileIndex = "ix_desktop_file_group_asc";
    private const string CaptureIndex = "ix_desktop_capture_group_asc";
    private const string MetadataQueueIndex = "ix_desktop_metadata_queue";

    [Fact]
    public async Task AdditiveIndexUpgradePreservesEveryTableAndExistingQuerySemantics()
    {
        var store = await CreateVersionFiveAsync();
        var queries = QueryCases().ToArray();
        var before = new List<string>();
        foreach (var query in queries) before.Add(await ViewSnapshotAsync(store, query));
        var contents = await AllTableContentsAsync();
        var oldIndexes = await IndexDefinitionsAsync();
        var schema = await SchemaWithoutIndexesAsync();
        Assert.DoesNotContain(FileIndex, oldIndexes.Keys);
        Assert.DoesNotContain(CaptureIndex, oldIndexes.Keys);
        Assert.DoesNotContain(MetadataQueueIndex, oldIndexes.Keys);

        await store.InitializeAsync();
        await store.InitializeAsync(); // Reopening an upgraded catalog must be idempotent.

        Assert.Equal(contents, await AllTableContentsAsync());
        Assert.Equal(schema, await SchemaWithoutIndexesAsync());
        var indexes = await IndexDefinitionsAsync();
        Assert.Equal(oldIndexes.Count + 3, indexes.Count);
        Assert.Equal(new[] { CaptureIndex, FileIndex, MetadataQueueIndex }.Order(StringComparer.Ordinal),
            indexes.Keys.Except(oldIndexes.Keys).Order(StringComparer.Ordinal));
        foreach (var index in oldIndexes) Assert.Equal(index.Value, indexes[index.Key]);
        for (var i = 0; i < queries.Length; i++)
            Assert.Equal(before[i], await ViewSnapshotAsync(store, queries[i]));
        Assert.Equal("ok", await ScalarAsync("PRAGMA integrity_check;"));

        // Search remains literal substring matching: SQL LIKE metacharacters are not wildcards.
        Assert.Equal(@"D:\Фото\100%.jpg", Assert.Single((await store.QueryPageAsync(new() { SearchText = "100%" })).Items).Path);
        Assert.Equal(@"D:\Фото\_literal_.jpg", Assert.Single((await store.QueryPageAsync(new() { SearchText = "_literal_" })).Items).Path);
        Assert.Empty((await store.QueryPageAsync(new() { SearchText = "quarantined.jpg" })).Items);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SearchGroupsUseCoveringIndexInEitherDateDirection(bool capture, bool descending)
    {
        var store = await CreateVersionFiveAsync(); await store.InitializeAsync();
        var month = capture ? "capture_month" : "file_month";
        var index = capture ? CaptureIndex : FileIndex;
        // Match QueryGroupsAsync's nested filtered source, including ordinary UI flags.
        var plan = await ExplainAsync($"""
            SELECT {month},COUNT(*) FROM
                (SELECT * FROM desktop_media_items WHERE is_quarantined=0 AND is_hidden_or_system=0
                    AND is_video=0 AND instr(search_key,$text)>0) AS items
            WHERE 1=1 GROUP BY {month} ORDER BY {month} {(descending ? "DESC" : "ASC")};
            """, ("$text", "NEEDLE"));
        Assert.Contains(plan, detail => detail.Contains("USING COVERING INDEX " + index, StringComparison.Ordinal));
        Assert.DoesNotContain(plan, detail => detail.Contains("TEMP B-TREE", StringComparison.Ordinal));
        var groups = await store.QueryGroupsAsync(new() { UseCaptureDate = capture, NewestFirst = descending, ShowVideos = false, SearchText = "needle" });
        Assert.NotEmpty(groups);
        Assert.Equal(await store.CountAsync(new() { ShowVideos = false, SearchText = "needle" }), groups.Sum(group => group.Count));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AscendingMonthPageSeeksWithinTheMonthIncludingNullGroups(bool capture, bool noDate)
    {
        var store = await CreateVersionFiveAsync(); await store.InitializeAsync();
        var month = capture ? "capture_month" : "file_month";
        var date = capture ? "COALESCE(capture_date_ticks,0)" : "file_local_ticks";
        var index = capture ? CaptureIndex : FileIndex;
        var groupPredicate = noDate ? month + " IS NULL" : month + "=$month";
        var plan = await ExplainAsync($"""
            SELECT * FROM (SELECT * FROM desktop_media_items WHERE is_quarantined=0 AND is_hidden_or_system=0) AS items
            WHERE {groupPredicate} ORDER BY {date} ASC,path_key ASC LIMIT 17 OFFSET 0;
            """, ("$month", "2026-12"));
        Assert.Contains(plan, detail => detail.Contains("SEARCH", StringComparison.Ordinal)
            && detail.Contains(index, StringComparison.Ordinal) && detail.Contains(month + "=?", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, detail => detail.Contains("TEMP B-TREE", StringComparison.Ordinal));
        var query = new CatalogViewQuery { UseCaptureDate = capture, NewestFirst = false, GroupKey = (capture ? "capture:" : "file:") + (noDate ? "none" : "2026-12"), PageSize = 17 };
        var page = await store.QueryPageAsync(query);
        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, item => Assert.Equal(noDate ? null : (int?)12, (capture ? item.CaptureDate : item.FileModifiedAt)?.Month));
    }

    [Fact]
    public async Task IndexCreationFailureRollsBackEarlierIndexAndLeavesCatalogRetryable()
    {
        var store = await CreateVersionFiveAsync();
        // An owned synthetic table creates a real SQLite DDL failure at the second new index.
        // Renaming the blocker later proves recovery without dropping catalog objects or rows.
        await ExecuteAsync($"CREATE TABLE {CaptureIndex}(sentinel TEXT NOT NULL); INSERT INTO {CaptureIndex} VALUES('keep me');");
        var contents = await AllTableContentsAsync(); var indexes = await IndexDefinitionsAsync();
        await Assert.ThrowsAsync<SqliteException>(() => store.InitializeAsync());
        Assert.Equal(contents, await AllTableContentsAsync());
        Assert.Equal(JsonSerializer.Serialize(indexes), JsonSerializer.Serialize(await IndexDefinitionsAsync()));
        Assert.DoesNotContain(FileIndex, (await IndexDefinitionsAsync()).Keys);
        Assert.Equal("ok", await ScalarAsync("PRAGMA integrity_check;"));

        await ExecuteAsync($"ALTER TABLE {CaptureIndex} RENAME TO owned_index_creation_blocker;");
        var retryContents = await AllTableContentsAsync();
        await store.InitializeAsync();
        Assert.Equal(retryContents, await AllTableContentsAsync());
        Assert.Contains(FileIndex, (await IndexDefinitionsAsync()).Keys);
        Assert.Contains(CaptureIndex, (await IndexDefinitionsAsync()).Keys);
        Assert.Contains(MetadataQueueIndex, (await IndexDefinitionsAsync()).Keys);
        Assert.Equal("keep me", await ScalarAsync("SELECT sentinel FROM owned_index_creation_blocker;"));
        Assert.Equal("ok", await ScalarAsync("PRAGMA integrity_check;"));
    }

    private static IEnumerable<CatalogViewQuery> QueryCases()
    {
        foreach (var capture in new[] { false, true })
        foreach (var descending in new[] { false, true })
        {
            var query = new CatalogViewQuery { UseCaptureDate = capture, NewestFirst = descending, PageSize = 53 };
            foreach (var text in new[] { "", "needle", "100%", "_literal_", "ёлка", "O'Hara", "does-not-exist" })
                yield return query with { SearchText = text };
            yield return query with { ViewMode = "Favorites", SearchText = "needle" };
            yield return query with { IncludeSystemFolders = true, ShowVideos = false, SearchText = "needle" };
            yield return query with { ViewMode = "Recent" };
            yield return query with { Folder = @"D:\Фото", ViewMode = "Folder", IncludeSubfolders = false };
            yield return query with { ExcludedFolders = [@"D:\Фото\nested"] };
            yield return query with { MissingCaptureDateOnly = true };
        }
    }

    private static async Task<string> ViewSnapshotAsync(SqliteDesktopCatalogStore store, CatalogViewQuery query)
    {
        var groups = await store.QueryGroupsAsync(query);
        var items = new List<SavedMediaItem>();
        await foreach (var item in store.EnumerateAsync(query)) items.Add(item);
        var pages = new List<string>();
        foreach (var group in groups)
        {
            var page = await store.QueryPageAsync(query, 0, 17, group.Key);
            pages.Add(JsonSerializer.Serialize(new { group.Key, page.Items, page.HasMore }));
        }
        var positions = new List<long?>();
        foreach (var item in items.Take(1).Concat(items.TakeLast(1)))
            positions.Add(await store.IndexOfAsync(query, item.Path));
        return JsonSerializer.Serialize(new { groups, items, pages, positions });
    }

    private async Task<SqliteDesktopCatalogStore> CreateVersionFiveAsync()
    {
        Directory.CreateDirectory(_root);
        // Frozen CREATE-only v0.10.8 layout: the upgrade test never removes an index.
        await ExecuteAsync(VersionFiveSchema + VersionFiveIndexes + "INSERT INTO desktop_settings(key,value) VALUES('catalog_schema','5');");
        var store = new SqliteDesktopCatalogStore(_root);
        var items = Enumerable.Range(0, 607).Select(i => new SavedMediaItem
        {
            Path = $@"D:\Фото\{(i % 3 == 0 ? "nested\\" : "")}{(i % 7 == 0 ? "needle_" : "photo_")}{i:D4}.jpg",
            SizeBytes = 1024 + i % 11,
            FileModifiedAt = i % 11 == 0 ? null : new DateTime(2026, 1 + i % 12, 1, 12, 0, 0, DateTimeKind.Local),
            CaptureDate = i % 5 == 0 ? null : new DateTime(2026, 1 + i % 12, 1),
            MetadataIndexed = true, IsFavorite = i % 13 == 0, IsVideo = i % 17 == 0, IsHiddenOrSystem = i % 19 == 0,
            FileIdentity = "synthetic-volume:file-" + i, Availability = FileAvailability.Available,
            AvailabilityCheckedAtUtc = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc)
        }).ToList();
        foreach (var name in new[] { "100%", "100x", "_literal_", "XliteralY", "ёлка", "O'Hara", "😀", "\uE000", "quarantined" })
            items.Add(new SavedMediaItem { Path = $@"D:\Фото\{name}.jpg", SizeBytes = 1024, IsFavorite = true,
                FileModifiedAt = new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Local), MetadataIndexed = true });
        await store.UpsertItemsAsync(items);
        await store.MoveItemAsync(@"D:\Фото\quarantined.jpg", @"Q:\quarantined.jpg", removeFromLibrary: true);
        await store.MoveItemAsync(items[1].Path, @"D:\Фото\renamed.jpg");
        var observed = (await store.GetItemAsync(items[2].Path))!;
        Assert.True(await store.SetAvailabilityAsync(observed, FileAvailability.AccessDenied, observed.AvailabilityCheckedAtUtc!.Value.AddMinutes(1), "synthetic-denied"));
        await store.SaveAsync(new() { WatchedFolders = [@"D:\Фото"], ExcludedFolders = [@"D:\Private"] }, saveItems: false);
        var hashes = new DuplicateHashStore(_root); await hashes.InitializeAsync();
        await hashes.SaveAsync([new() { Path = items[3].Path, SizeBytes = items[3].SizeBytes, FileModifiedAt = items[3].FileModifiedAt, Hash = new string('A', 64) }]);
        var metadata = new MetadataIndexStore(_root); await metadata.InitializeAsync();
        await metadata.SaveAsync(items[4].Path, items[4].SizeBytes, items[4].FileModifiedAt, items[4].CaptureDate);
        return store;
    }

    private SqliteConnection Connection() => new(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(_root, "catalog-v2.sqlite"), Pooling = false
    }.ToString());

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = Connection(); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = Connection(); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync();
    }

    private async Task<List<string>> ExplainAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = Connection(); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = new List<string>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(3));
        return result;
    }

    private async Task<SortedDictionary<string, string?>> IndexDefinitionsAsync()
    {
        await using var connection = Connection(); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT name,sql FROM sqlite_schema WHERE type='index' ORDER BY name;";
        var result = new SortedDictionary<string, string?>(StringComparer.Ordinal); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        return result;
    }

    private async Task<string> SchemaWithoutIndexesAsync()
    {
        await using var connection = Connection(); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT type,name,sql FROM sqlite_schema WHERE type<>'index' ORDER BY name;";
        var result = new List<object[]>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { var values = new object[reader.FieldCount]; reader.GetValues(values); result.Add(values); }
        return JsonSerializer.Serialize(result);
    }

    private async Task<string> AllTableContentsAsync()
    {
        await using var connection = Connection(); await connection.OpenAsync();
        var names = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        }
        var result = new SortedDictionary<string, List<object?[]>>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\" ORDER BY rowid;";
            var rows = new List<object?[]>(); await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            result.Add(name, rows);
        }
        return JsonSerializer.Serialize(result);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string VersionFiveIndexes = """
        CREATE UNIQUE INDEX IF NOT EXISTS ix_desktop_path_key ON desktop_media_items(path_key);
        CREATE INDEX IF NOT EXISTS ix_desktop_file_sort ON desktop_media_items(is_quarantined,file_local_ticks DESC,path_key,is_hidden_or_system,is_video,is_favorite);
        CREATE INDEX IF NOT EXISTS ix_desktop_file_sort_asc ON desktop_media_items(is_quarantined,file_local_ticks ASC,path_key,is_hidden_or_system,is_video,is_favorite);
        CREATE INDEX IF NOT EXISTS ix_desktop_capture_sort ON desktop_media_items(is_quarantined,COALESCE(capture_date_ticks,0) DESC,path_key,is_hidden_or_system,is_video,is_favorite);
        CREATE INDEX IF NOT EXISTS ix_desktop_capture_sort_asc ON desktop_media_items(is_quarantined,COALESCE(capture_date_ticks,0) ASC,path_key,is_hidden_or_system,is_video,is_favorite);
        CREATE INDEX IF NOT EXISTS ix_desktop_folder ON desktop_media_items(folder_key,file_local_ticks DESC,path_key);
        CREATE INDEX IF NOT EXISTS ix_desktop_folder_path ON desktop_media_items(folder_key,path_key);
        CREATE INDEX IF NOT EXISTS ix_desktop_file_group ON desktop_media_items(is_quarantined,file_month,file_local_ticks DESC,path_key,is_hidden_or_system,is_video,is_favorite);
        CREATE INDEX IF NOT EXISTS ix_desktop_capture_group ON desktop_media_items(is_quarantined,capture_month,COALESCE(capture_date_ticks,0) DESC,path_key,is_hidden_or_system,is_video,is_favorite);
        CREATE INDEX IF NOT EXISTS ix_desktop_metadata_due ON desktop_media_items(metadata_indexed,is_quarantined,is_video,metadata_retry_ticks);
        CREATE INDEX IF NOT EXISTS ix_desktop_size ON desktop_media_items(size_bytes,is_quarantined);
        CREATE INDEX IF NOT EXISTS ix_desktop_favorites ON desktop_media_items(is_favorite,file_local_ticks DESC,path_key);
        CREATE INDEX IF NOT EXISTS ix_desktop_file_identity ON desktop_media_items(file_identity) WHERE file_identity IS NOT NULL;
        """;
    private const string VersionFiveSchema = """

        CREATE TABLE IF NOT EXISTS desktop_media_items (
            asset_id TEXT NOT NULL PRIMARY KEY, path TEXT NOT NULL UNIQUE, path_key TEXT NOT NULL UNIQUE,
            folder_key TEXT NOT NULL, search_key TEXT NOT NULL, is_favorite INTEGER NOT NULL DEFAULT 0,
            size_bytes INTEGER NOT NULL, file_modified_utc_ticks INTEGER NOT NULL, file_local_ticks INTEGER NOT NULL DEFAULT 0,
            file_month TEXT NULL, capture_date_ticks INTEGER NULL, capture_month TEXT NULL,
            metadata_indexed INTEGER NOT NULL DEFAULT 0, metadata_status INTEGER NOT NULL DEFAULT 0,
            metadata_attempted_ticks INTEGER NULL, metadata_retry_ticks INTEGER NULL, metadata_error_code TEXT NULL, is_video INTEGER NOT NULL DEFAULT 0,
            is_hidden_or_system INTEGER NOT NULL DEFAULT 0, is_quarantined INTEGER NOT NULL DEFAULT 0, last_seen_utc TEXT NOT NULL,
            availability INTEGER NOT NULL DEFAULT 4, availability_checked_ticks INTEGER NULL, availability_error_code TEXT NULL,
            file_identity TEXT NULL, observation_version INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS desktop_size_counts(size_bytes INTEGER NOT NULL PRIMARY KEY,item_count INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_desktop_duplicate_sizes ON desktop_size_counts(size_bytes) WHERE item_count>1;
        CREATE TRIGGER IF NOT EXISTS desktop_size_insert AFTER INSERT ON desktop_media_items WHEN NEW.is_quarantined=0
        BEGIN
            INSERT INTO desktop_size_counts(size_bytes,item_count) VALUES(NEW.size_bytes,1)
            ON CONFLICT(size_bytes) DO UPDATE SET item_count=item_count+1;
        END;
        CREATE TRIGGER IF NOT EXISTS desktop_size_delete AFTER DELETE ON desktop_media_items WHEN OLD.is_quarantined=0
        BEGIN
            UPDATE desktop_size_counts SET item_count=item_count-1 WHERE size_bytes=OLD.size_bytes;
            DELETE FROM desktop_size_counts WHERE size_bytes=OLD.size_bytes AND item_count=0;
        END;
        CREATE TRIGGER IF NOT EXISTS desktop_size_update AFTER UPDATE OF size_bytes,is_quarantined ON desktop_media_items
        WHEN OLD.size_bytes<>NEW.size_bytes OR OLD.is_quarantined<>NEW.is_quarantined
        BEGIN
            UPDATE desktop_size_counts SET item_count=item_count-1 WHERE size_bytes=OLD.size_bytes AND OLD.is_quarantined=0;
            INSERT INTO desktop_size_counts(size_bytes,item_count) SELECT NEW.size_bytes,1 WHERE NEW.is_quarantined=0
            ON CONFLICT(size_bytes) DO UPDATE SET item_count=item_count+1;
            DELETE FROM desktop_size_counts WHERE size_bytes=OLD.size_bytes AND item_count=0;
        END;
        CREATE TABLE IF NOT EXISTS desktop_settings(key TEXT NOT NULL PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS desktop_excluded_folders(path TEXT NOT NULL PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS desktop_move_receipts(
            source_key TEXT NOT NULL,destination_key TEXT NOT NULL,quarantined INTEGER NOT NULL,
            asset_id TEXT NULL,committed_at_utc TEXT NOT NULL,PRIMARY KEY(source_key,destination_key,quarantined)
        );
        """;
}
