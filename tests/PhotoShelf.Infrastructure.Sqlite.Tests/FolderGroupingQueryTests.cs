using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class FolderGroupingQueryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-folder-groups-" + Guid.NewGuid().ToString("N"));
    private const string Folder = @"D:\Фото_100%\Ёлка";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SmallFolderGroupingPreservesFiltersDatesLiteralSearchAndAllRows(bool recursive)
    {
        var (store, items) = await CreateMixedAsync();
        var before = await MediaContentsAsync();
        foreach (var capture in new[] { false, true })
        foreach (var descending in new[] { false, true })
        foreach (var includeHidden in new[] { false, true })
        foreach (var videos in new[] { false, true })
        foreach (var search in new[] { "", "needle", "_100%", "ёж", "nested/", "absent" })
        {
            var query = new CatalogViewQuery
            {
                ViewMode = "Folder", Folder = Folder.ToLowerInvariant(), IncludeSubfolders = recursive,
                UseCaptureDate = capture, NewestFirst = descending, IncludeSystemFolders = includeHidden,
                ShowVideos = videos, SearchText = search, ExcludedFolders = [Folder]
            };
            var expected = ExpectedGroups(items, query);
            var actual = await store.QueryGroupsAsync(query);
            Assert.Equal(expected, actual);
        }
        foreach (var capture in new[] { false, true })
        {
            var missing = new CatalogViewQuery { ViewMode = "Folder", Folder = Folder, IncludeSubfolders = recursive,
                UseCaptureDate = capture, MissingCaptureDateOnly = true };
            Assert.Equal(ExpectedGroups(items, missing), await store.QueryGroupsAsync(missing));
        }
        Assert.Equal(before, await MediaContentsAsync());
        Assert.Equal("ok", await ScalarAsync("PRAGMA integrity_check;"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SmallFolderGroupCommandSeeksTheExistingRangeIndex(bool recursive, bool capture)
    {
        var (store, _) = await CreateMixedAsync();
        var query = new CatalogViewQuery { ViewMode = "Folder", Folder = Folder, IncludeSubfolders = recursive, UseCaptureDate = capture, NewestFirst = false, SearchText = "needle" };
        await using var connection = Connection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = await GroupCommandAsync(query, connection, transaction);
        var index = recursive ? "ix_desktop_path_key" : "ix_desktop_folder_path";
        Assert.Contains("INDEXED BY " + index, command.CommandText);
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        var plan = await PlanAsync(command);
        Assert.Contains(plan, line => line.Contains("SEARCH", StringComparison.Ordinal)
            && line.Contains(index, StringComparison.Ordinal)
            && line.Contains(recursive ? "path_key>? AND path_key<?" : "folder_key=?", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("SCAN desktop_media_items", StringComparison.Ordinal));
        // The plan is obtained from the very command builder used by the public method.
        Assert.NotEmpty(await store.QueryGroupsAsync(query));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "needle")]
    [InlineData(true, "")]
    [InlineData(true, "needle")]
    public async Task WholeLibraryRootRetainsCoveringGroupPlanner(bool capture, string search)
    {
        var (store, items) = await CreateMixedAsync();
        var query = new CatalogViewQuery { ViewMode = "Folder", Folder = @"D:\", UseCaptureDate = capture, SearchText = search, NewestFirst = false };
        await using var connection = Connection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = await GroupCommandAsync(query, connection, transaction);
        Assert.DoesNotContain("INDEXED BY", command.CommandText);
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        var plan = await PlanAsync(command);
        Assert.Contains(plan, line => line.Contains("USING COVERING INDEX ix_desktop_" + (capture ? "capture" : "file") + "_group", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
        Assert.Equal(ExpectedGroups(items, query), await store.QueryGroupsAsync(query));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public async Task ScopeThresholdIncludesOneSixteenthAndStopsAtTheNextEntry(int inside, bool expectedSeek)
    {
        var store = new SqliteDesktopCatalogStore(_root); await store.InitializeAsync();
        await store.UpsertItemsAsync(Enumerable.Range(0, 1600).Select(i => Item($@"D:\{(i < inside ? "small" : "outside")}\{i:D4}.jpg", i)));
        var query = new CatalogViewQuery { ViewMode = "Folder", Folder = @"D:\small" };
        await using var connection = Connection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = await GroupCommandAsync(query, connection, transaction);
        Assert.Equal(expectedSeek, command.CommandText.Contains("INDEXED BY ix_desktop_path_key", StringComparison.Ordinal));
        Assert.Equal(inside, (await store.QueryGroupsAsync(query)).Sum(group => group.Count));
    }

    [Fact]
    public async Task EstimationAndGroupingUseOneSnapshotWhileCatalogChanges()
    {
        var (store, items) = await CreateMixedAsync();
        var query = new CatalogViewQuery { ViewMode = "Folder", Folder = Folder, UseCaptureDate = true };
        var expected = ExpectedGroups(items, query);
        await using (var connection = Connection())
        {
            await connection.OpenAsync(); await using var transaction = connection.BeginTransaction(deferred: true);
            // Building the command establishes the same read snapshot through both probes.
            await using var command = await GroupCommandAsync(query, connection, transaction);
            var added = Item(Folder + @"\new-after-estimation.jpg", 1);
            await store.UpsertItemsAsync([added]); items.Add(added);
            long count = 0; await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) count += reader.GetInt64(1);
            Assert.Equal(expected.Sum(group => group.Count), count);
        }
        Assert.Equal(ExpectedGroups(items, query), await store.QueryGroupsAsync(query));
    }

    [Theory]
    [InlineData("All")]
    [InlineData("Favorites")]
    [InlineData("Recent")]
    public async Task NonFolderViewsKeepTheirOriginalSource(string view)
    {
        var (store, _) = await CreateMixedAsync();
        var query = new CatalogViewQuery { ViewMode = view, Folder = Folder, UseCaptureDate = true, SearchText = "needle" };
        await using var connection = Connection(); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = await GroupCommandAsync(query, connection, transaction);
        Assert.DoesNotContain("INDEXED BY", command.CommandText);
        Assert.Equal(await store.CountAsync(query), (await store.QueryGroupsAsync(query)).Sum(group => group.Count));
    }

    [Fact]
    public async Task EmptyCatalogReturnsNoGroupsWithoutChangingRows()
    {
        var store = new SqliteDesktopCatalogStore(_root); await store.InitializeAsync();
        var query = new CatalogViewQuery { ViewMode = "Folder", Folder = @"C:\" };
        var before = await MediaContentsAsync();
        Assert.Empty(await store.QueryGroupsAsync(query));
        Assert.Equal(before, await MediaContentsAsync());
        Assert.Equal("ok", await ScalarAsync("PRAGMA integrity_check;"));
    }

    private async Task<(SqliteDesktopCatalogStore Store, List<SavedMediaItem> Items)> CreateMixedAsync()
    {
        var store = new SqliteDesktopCatalogStore(_root); await store.InitializeAsync();
        var items = Enumerable.Range(0, 32).Select(i =>
        {
            var item = Item($"{Folder}\\{(i % 2 == 0 ? "nested\\" : "")}{(i % 3 == 0 ? "needle_ёж" : "photo")}_{i:D2}.jpg", i);
            item.IsHiddenOrSystem = i % 5 == 0; item.IsVideo = i % 7 == 0; return item;
        }).ToList();
        items.AddRange(Enumerable.Range(0, 1024).Select(i => Item($@"D:\Other\{i:D4}.jpg", i)));
        items.Add(Item(Folder + @"2\needle-neighbor.jpg", 1));
        items.Add(Item(Folder + @"\ЁЖ_100%.jpg", 1));
        items.Add(Item(Folder + @"\😀.jpg", 1));
        items.Add(Item(Folder + "\\\uE000.jpg", 1));
        await store.UpsertItemsAsync(items);
        var quarantined = Item(Folder + @"\needle-quarantine.jpg", 1);
        await store.UpsertItemsAsync([quarantined]);
        await store.MoveItemAsync(quarantined.Path, Folder + @"\needle-quarantine-removed.jpg", removeFromLibrary: true);
        return (store, items);
    }

    private static SavedMediaItem Item(string path, int i) => new()
    {
        Path = path, SizeBytes = 42,
        FileModifiedAt = i % 11 == 0 ? null : new DateTime(2026, 1 + i % 4, 1, 12, 0, 0, DateTimeKind.Local),
        CaptureDate = i % 3 == 0 ? null : new DateTime(2020, 1 + i % 4, 1),
        MetadataIndexed = true, IsFavorite = true
    };

    private static IReadOnlyList<CatalogDateGroup> ExpectedGroups(IEnumerable<SavedMediaItem> items, CatalogViewQuery query)
    {
        static string Key(string value) => value.Replace('\\', '/').TrimEnd('/').ToUpperInvariant();
        var folder = Key(query.Folder!); var search = query.SearchText.Replace('\\', '/').ToUpperInvariant();
        var filtered = items.Where(item =>
        {
            var path = Key(item.Path); var parent = path[..path.LastIndexOf('/')];
            return (query.IncludeSubfolders ? path.StartsWith(folder + "/", StringComparison.Ordinal) : parent == folder)
                && (query.IncludeSystemFolders || !item.IsHiddenOrSystem) && (query.ShowVideos || !item.IsVideo)
                && (!query.MissingCaptureDateOnly || item.CaptureDate is null && !item.IsVideo)
                && (search.Length == 0 || path.Contains(search, StringComparison.Ordinal));
        });
        var groups = filtered.GroupBy(item => (query.UseCaptureDate ? item.CaptureDate : item.FileModifiedAt)?.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        var ordered = query.NewestFirst ? groups.OrderByDescending(group => group.Key, StringComparer.Ordinal) : groups.OrderBy(group => group.Key, StringComparer.Ordinal);
        return ordered.Select(group => new CatalogDateGroup((query.UseCaptureDate ? "capture:" : "file:") + (group.Key ?? "none"),
            group.Key is null ? null : int.Parse(group.Key[..4], CultureInfo.InvariantCulture),
            group.Key is null ? null : int.Parse(group.Key[5..], CultureInfo.InvariantCulture), group.LongCount())).ToArray();
    }

    private static Task<SqliteCommand> GroupCommandAsync(CatalogViewQuery query, SqliteConnection connection, SqliteTransaction transaction)
        => (Task<SqliteCommand>)typeof(SqliteDesktopCatalogStore).GetMethod("BuildGroupsCommandAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [query, connection, transaction, CancellationToken.None])!;

    private static async Task<List<string>> PlanAsync(SqliteCommand command)
    {
        var result = new List<string>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(3));
        return result;
    }

    private SqliteConnection Connection() => new(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(_root, "catalog-v2.sqlite"), Pooling = false
    }.ToString());

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = Connection(); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync();
    }

    private async Task<string> MediaContentsAsync()
    {
        await using var connection = Connection(); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM desktop_media_items ORDER BY asset_id;";
        var result = new List<object?[]>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            result.Add(row);
        }
        return JsonSerializer.Serialize(result);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
