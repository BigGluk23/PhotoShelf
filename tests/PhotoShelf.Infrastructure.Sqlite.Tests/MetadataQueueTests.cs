using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Metadata;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class MetadataQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-metadata-queue-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Due = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime KnownDate = new(2020, 1, 2);
    private static CatalogViewQuery Scope => new() { IncludeSystemFolders = true, MetadataDueAtUtc = Due };
    private SqliteConnection Connection() => new($"Data Source={Path.Combine(_root, "catalog-v2.sqlite")};Pooling=False");

    [Fact]
    public async Task SparseQueuePagesSkipTerminalRowsAndDoNotLoseRowsWhenEachPageCommits()
    {
        var store = await CreateAsync();
        await store.UpsertItemsAsync(Enumerable.Range(0, 10000).Select(i => Item(i, terminal: i >= 277)));
        await store.ResetMetadataIndexAsync();
        // Reset preserves prior status, so indexed=0, not status=Pending, defines work.
        await ExecuteAsync("UPDATE desktop_media_items SET metadata_indexed=1 WHERE path_key>='D:/PHOTO/00000277.JPG';");
        var before = await NonMetadataContentsAsync();
        Assert.Equal(277, (await store.GetMetadataQueueSummaryAsync(Scope)).DueCount);
        var paths = new List<string>(); string? cursor = null;
        while (true)
        {
            var page = await store.QueryMetadataDuePageAsync(Scope, cursor, 64);
            if (page.Count == 0) break;
            Assert.InRange(page.Count, 1, 64);
            cursor = page[^1].Path; paths.AddRange(page.Select(item => item.Path));
            var accepted = await store.UpdateMetadataResultsAsync(page.Select(item => new MetadataCatalogUpdate(item,
                new(MetadataReadStatus.Absent), Due)).ToArray());
            Assert.All(accepted, value => Assert.True(value));
        }
        Assert.Equal(277, paths.Count); Assert.Equal(277, paths.Distinct().Count());
        Assert.Equal(new MetadataQueueSummary(0, 0, null), await store.GetMetadataQueueSummaryAsync(Scope));
        Assert.Equal(before, await NonMetadataContentsAsync());
        Assert.Equal("ok", await ScalarAsync("PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task RetrySummaryMatchesEligibilityAndFixedPassDoesNotImmediatelyRetryFailures()
    {
        var store = await CreateAsync();
        await store.UpsertItemsAsync(Enumerable.Range(0, 11).Select(i => Item(i)));
        await ExecuteAsync($"""
            UPDATE desktop_media_items SET metadata_retry_ticks={Due.AddMinutes(-1).Ticks} WHERE path_key LIKE '%00000001.JPG';
            UPDATE desktop_media_items SET metadata_retry_ticks={Due.Ticks} WHERE path_key LIKE '%00000002.JPG';
            UPDATE desktop_media_items SET metadata_retry_ticks={Due.AddMinutes(1).Ticks} WHERE path_key LIKE '%00000003.JPG';
            UPDATE desktop_media_items SET metadata_retry_ticks={Due.AddMinutes(2).Ticks} WHERE path_key LIKE '%00000004.JPG';
            UPDATE desktop_media_items SET is_quarantined=1 WHERE path_key LIKE '%00000005.JPG';
            UPDATE desktop_media_items SET is_video=1 WHERE path_key LIKE '%00000006.JPG';
            UPDATE desktop_media_items SET availability=1 WHERE path_key LIKE '%00000007.JPG';
            UPDATE desktop_media_items SET availability=4,availability_error_code='unsafe-reparse' WHERE path_key LIKE '%00000008.JPG';
            UPDATE desktop_media_items SET metadata_indexed=1 WHERE path_key LIKE '%00000009.JPG';
            UPDATE desktop_media_items SET availability=3 WHERE path_key LIKE '%00000010.JPG';
            """);
        var before = await AllContentsAsync();
        var page = await store.QueryMetadataDuePageAsync(Scope);
        Assert.Equal(new[] { Item(0).Path, Item(1).Path, Item(2).Path }, page.Select(item => item.Path));
        Assert.Equal(new MetadataQueueSummary(3, 2, Due.AddMinutes(1)), await store.GetMetadataQueueSummaryAsync(Scope));
        Assert.Equal(before, await AllContentsAsync());
        var result = await store.UpdateMetadataResultsAsync(page.Select(item => new MetadataCatalogUpdate(item,
            new(MetadataReadStatus.TransientError, ErrorCode: "locked"), Due)).ToArray());
        Assert.All(result, value => Assert.True(value));
        Assert.Empty(await store.QueryMetadataDuePageAsync(Scope));
        Assert.Equal(new MetadataQueueSummary(0, 5, Due.AddMinutes(1)), await store.GetMetadataQueueSummaryAsync(Scope));
        Assert.Equal(5, (await store.QueryMetadataDuePageAsync(Scope with { MetadataDueAtUtc = Due.AddMinutes(5) })).Count);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(20)]
    public async Task QueueAndSummaryPreserveNestedInclusionRulesAndViewFilters(int ruleCount)
    {
        var store = await CreateAsync();
        var items = Enumerable.Range(0, ruleCount).SelectMany(i => new[]
        {
            WithPath(Item(i * 3), $@"D:\excluded-{i:D2}\skip.jpg"),
            WithPath(Item(i * 3 + 1), $@"D:\excluded-{i:D2}\keep\photo.jpg"),
            WithPath(Item(i * 3 + 2), $@"D:\outside-{i:D2}\photo.jpg")
        }).ToArray();
        await store.UpsertItemsAsync(items);
        var scope = Scope with
        {
            ExcludedFolders = Enumerable.Range(0, ruleCount).Select(i => $@"D:\excluded-{i:D2}").ToArray(),
            IncludedFolders = Enumerable.Range(0, ruleCount).Select(i => $@"D:\excluded-{i:D2}\keep").ToArray()
        };
        foreach (var query in new[] { scope, scope with { ViewMode = "Favorites" }, scope with { IncludeSystemFolders = false },
            scope with { SearchText = "keep/" }, scope with { ViewMode = "Folder", Folder = @"D:\excluded-00", IncludeSubfolders = false },
            scope with { ViewMode = "Folder", Folder = @"D:\excluded-00", IncludeSubfolders = true } })
        {
            var expected = new List<string>();
            await foreach (var item in store.EnumerateAsync(query)) expected.Add(item.Path);
            var actual = new List<string>(); string? cursor = null;
            while (true)
            {
                var page = await store.QueryMetadataDuePageAsync(query, cursor, 7);
                if (page.Count == 0) break;
                actual.AddRange(page.Select(item => item.Path)); cursor = page[^1].Path;
            }
            Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
            Assert.Equal(expected.Count, (await store.GetMetadataQueueSummaryAsync(query)).DueCount);
        }
        // The long-rule path still retains the partial queue index, not the all-media UNION.
        await using var connection = Connection(); await connection.OpenAsync();
        await using var command = PageCommand(scope, items[0].Path, connection);
        var plan = await ExplainAsync(command);
        Assert.Contains(plan, line => line.Contains("ix_desktop_metadata_queue", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
        Assert.DoesNotContain("UNION ALL", command.CommandText);
    }

    [Fact]
    public async Task PageSeeksPendingPathIndexAndSummaryScansOnlyPendingIndex()
    {
        var store = await CreateAsync();
        await store.UpsertItemsAsync([Item(0), Item(1, terminal: true)]);
        await using var connection = Connection(); await connection.OpenAsync();
        await using var page = PageCommand(Scope, Item(0).Path, connection);
        var pagePlan = await ExplainAsync(page);
        Assert.Contains(pagePlan, line => line.Contains("SEARCH", StringComparison.Ordinal)
            && line.Contains("ix_desktop_metadata_queue (path_key>?)", StringComparison.Ordinal));
        Assert.DoesNotContain(pagePlan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
        var method = typeof(SqliteDesktopCatalogStore).GetMethod("BuildMetadataSummaryCommand", BindingFlags.Static | BindingFlags.NonPublic)!;
        await using var summary = (SqliteCommand)method.Invoke(null, [Scope, Due, connection, CancellationToken.None])!;
        var summaryPlan = await ExplainAsync(summary);
        Assert.Contains(summaryPlan, line => line.Contains("ix_desktop_metadata_queue", StringComparison.Ordinal));
        Assert.DoesNotContain(summaryPlan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("asset_id='replacement'")]
    [InlineData("size_bytes=size_bytes+1")]
    [InlineData("file_modified_utc_ticks=file_modified_utc_ticks+1")]
    [InlineData("observation_version=observation_version+1")]
    [InlineData("availability=1")]
    [InlineData("availability=2")]
    [InlineData("availability=3")]
    [InlineData("availability=4,availability_error_code='unsafe-reparse'")]
    [InlineData("is_quarantined=1")]
    [InlineData("is_video=1")]
    [InlineData("metadata_attempted_ticks=1234")]
    [InlineData("path_key=path_key||'RENAME'")]
    public async Task BatchRejectsChangedCatalogSnapshotWithoutOverwritingKnownMetadata(string mutation)
    {
        var store = await CreateAsync(); await store.UpsertItemsAsync([Item(0), Item(1)]);
        var expected = await store.QueryMetadataDuePageAsync(Scope);
        await ExecuteAsync($"UPDATE desktop_media_items SET {mutation} WHERE path_key='D:/PHOTO/00000000.JPG';");
        var rejectedBefore = await ScalarAsync("SELECT capture_date_ticks||'|'||metadata_indexed||'|'||COALESCE(metadata_attempted_ticks,'null') FROM desktop_media_items WHERE path_key='D:/PHOTO/00000000.JPG' OR path_key='D:/PHOTO/00000000.JPGRENAME';");
        var mask = await store.UpdateMetadataResultsAsync(expected.Select(item => new MetadataCatalogUpdate(item, new(MetadataReadStatus.Absent), Due)).ToArray());
        Assert.Equal(new[] { false, true }, mask);
        Assert.Equal(rejectedBefore, await ScalarAsync("SELECT capture_date_ticks||'|'||metadata_indexed||'|'||COALESCE(metadata_attempted_ticks,'null') FROM desktop_media_items WHERE path_key='D:/PHOTO/00000000.JPG' OR path_key='D:/PHOTO/00000000.JPGRENAME';"));
        Assert.Null((await store.GetItemAsync(Item(1).Path))!.CaptureDate);
    }

    [Theory]
    [InlineData(MetadataReadStatus.TransientError)]
    [InlineData(MetadataReadStatus.Unsupported)]
    [InlineData(MetadataReadStatus.Corrupt)]
    public async Task FailedBatchReadsRetainDatesFavoritesIdentityAndFingerprint(MetadataReadStatus status)
    {
        var store = await CreateAsync(); await store.UpsertItemsAsync([Item(0)]);
        var before = (await store.QueryMetadataDuePageAsync(Scope))[0];
        var preserved = await NonMetadataContentsAsync();
        Assert.Equal(new[] { true }, await store.UpdateMetadataResultsAsync([new(before, new(status, ErrorCode: "read-error"), Due)]));
        var after = (await store.GetItemAsync(before.Path))!;
        Assert.Equal(KnownDate, after.CaptureDate); Assert.Equal(status, after.MetadataStatus);
        Assert.Equal(status != MetadataReadStatus.TransientError, after.MetadataIndexed);
        Assert.Equal(status == MetadataReadStatus.TransientError ? Due.AddMinutes(5) : (DateTime?)null, after.MetadataRetryAtUtc);
        Assert.Equal(preserved, await NonMetadataContentsAsync());
    }

    [Fact]
    public async Task ErrorAfterFirstUpdateRollsBackWholeBatch()
    {
        var store = await CreateAsync(); await store.UpsertItemsAsync([Item(0), Item(1)]);
        var expected = await store.QueryMetadataDuePageAsync(Scope); var before = await AllContentsAsync();
        await ExecuteAsync("""
            CREATE TRIGGER reject_second_metadata BEFORE UPDATE OF metadata_status ON desktop_media_items
            WHEN NEW.path_key='D:/PHOTO/00000001.JPG'
            BEGIN SELECT RAISE(ABORT,'synthetic metadata failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => store.UpdateMetadataResultsAsync(expected.Select(item =>
            new MetadataCatalogUpdate(item, new(MetadataReadStatus.Absent), Due)).ToArray()));
        Assert.Equal(before, await AllContentsAsync());
        Assert.Equal("ok", await ScalarAsync("PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task CancellationAfterFirstSqlUpdateRollsBackAllAcceptedRows()
    {
        var store = await CreateAsync(); await store.UpsertItemsAsync([Item(0), Item(1)]);
        var expected = await store.QueryMetadataDuePageAsync(Scope); var before = await AllContentsAsync();
        using var cancellation = new CancellationTokenSource();
        await using var connection = Connection(); await connection.OpenAsync();
        var called = 0;
        connection.CreateFunction("cancel_metadata_batch", () => { called++; cancellation.Cancel(); return 0; });
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TEMP TRIGGER cancel_after_metadata AFTER UPDATE OF metadata_status ON desktop_media_items
                BEGIN SELECT cancel_metadata_batch(); END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }
        var method = typeof(SqliteDesktopCatalogStore).GetMethod("ExecuteMetadataBatchAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var updates = expected.Select(item => new MetadataCatalogUpdate(item, new(MetadataReadStatus.Absent), Due)).ToArray();
        var task = (Task<IReadOnlyList<bool>>)method.Invoke(null, [connection, updates, cancellation.Token])!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, called); Assert.Equal(before, await AllContentsAsync());
    }

    [Fact]
    public async Task LimitsAndAlreadyCancelledCallsLeaveCatalogUnchanged()
    {
        var store = await CreateAsync(); await store.UpsertItemsAsync([Item(0)]);
        var item = (await store.QueryMetadataDuePageAsync(Scope))[0]; var before = await AllContentsAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.QueryMetadataDuePageAsync(Scope, pageSize: 129));
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryMetadataDuePageAsync(new()));
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryMetadataDuePageAsync(Scope with { ViewMode = "Recent" }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetMetadataQueueSummaryAsync(Scope with { ViewMode = "Recent" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = store.UpdateMetadataResultsAsync(Enumerable.Repeat(new MetadataCatalogUpdate(item, new(MetadataReadStatus.Absent), Due), 129).ToArray()); });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateMetadataResultsAsync([new(item, new(MetadataReadStatus.Absent), Due)], cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.QueryMetadataDuePageAsync(Scope, token: cancellation.Token));
        Assert.Equal(before, await AllContentsAsync());
    }

    [Fact]
    public async Task BackgroundPauseSettingDefaultsFalseAndRoundTripsWithoutChangingMedia()
    {
        var store = await CreateAsync(); await store.UpsertItemsAsync([Item(0)]);
        Assert.False((await store.LoadAsync(includeItems: false)).BackgroundProcessingPaused);
        var before = await AllContentsAsync();
        await store.SaveAsync(new() { BackgroundProcessingPaused = true }, saveItems: false);
        Assert.True((await new SqliteDesktopCatalogStore(_root).LoadAsync(includeItems: false)).BackgroundProcessingPaused);
        await store.SaveAsync(new() { BackgroundProcessingPaused = false }, saveItems: false);
        Assert.False((await store.LoadAsync(includeItems: false)).BackgroundProcessingPaused);
        Assert.Equal(before, await AllContentsAsync());
    }

    private async Task<SqliteDesktopCatalogStore> CreateAsync()
    {
        var store = new SqliteDesktopCatalogStore(_root); await store.InitializeAsync(); return store;
    }
    private static SavedMediaItem WithPath(SavedMediaItem item, string path) { item.Path = path; return item; }
    private static SavedMediaItem Item(int index, bool terminal = false) => new()
    {
        AssetId = "synthetic-" + index, Path = $@"D:\Photo\{index:D8}.jpg", SizeBytes = 42,
        FileModifiedAt = Due.AddDays(index), CaptureDate = KnownDate, MetadataIndexed = terminal,
        MetadataStatus = MetadataReadStatus.Found, Availability = FileAvailability.Available,
        IsFavorite = index % 2 == 0, IsHiddenOrSystem = index % 3 == 0, FileIdentity = "synthetic-id-" + index
    };
    private static SqliteCommand PageCommand(CatalogViewQuery scope, string? afterPath, SqliteConnection connection)
    {
        var method = typeof(SqliteDesktopCatalogStore).GetMethod("BuildMetadataPageCommand", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (SqliteCommand)method.Invoke(null, [scope, afterPath, 128, connection, CancellationToken.None])!;
    }
    private static async Task<List<string>> ExplainAsync(SqliteCommand command)
    {
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        var result = new List<string>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(3)); return result;
    }
    private async Task ExecuteAsync(string sql)
    {
        await using var connection = Connection(); await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private async Task<string> ScalarAsync(string sql)
    {
        await using var connection = Connection(); await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = sql; return Convert.ToString(await command.ExecuteScalarAsync())!;
    }
    private Task<string> AllContentsAsync() => ContentsAsync("*");
    private Task<string> NonMetadataContentsAsync() => ContentsAsync("asset_id,path,path_key,folder_key,search_key,is_favorite,size_bytes,file_modified_utc_ticks,file_local_ticks,file_month,is_video,is_hidden_or_system,is_quarantined,last_seen_utc,availability,availability_checked_ticks,availability_error_code,file_identity,observation_version");
    private async Task<string> ContentsAsync(string columns)
    {
        await using var connection = Connection(); await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {columns} FROM desktop_media_items ORDER BY asset_id;";
        var rows = new List<object?[]>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); }
        return JsonSerializer.Serialize(rows);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
