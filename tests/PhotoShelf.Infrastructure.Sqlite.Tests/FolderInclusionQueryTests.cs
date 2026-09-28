using System.Text.Json;
using System.Reflection;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class FolderInclusionQueryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-folder-rules-" + Guid.NewGuid().ToString("N"));
    private static SavedMediaItem Item(string path) => new()
    {
        Path = path, SizeBytes = 42, IsFavorite = true,
        FileModifiedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Local)
    };
    private async Task<SqliteDesktopCatalogStore> CreateAsync(params string[] paths)
    {
        var store = new SqliteDesktopCatalogStore(_root);
        await store.InitializeAsync(); await store.UpsertItemsAsync(paths.Select(Item)); return store;
    }
    private static async Task<string[]> PathsAsync(SqliteDesktopCatalogStore store, CatalogViewQuery query)
    {
        var paths = new List<string>();
        await foreach (var item in store.EnumerateAsync(query)) paths.Add(item.Path);
        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task CheckingChildKeepsDriveSiblingsExcludedAndNestedOverridesApplyToEveryCatalogQuery()
    {
        var selected = new[] { @"C:\Фото\a.jpg", @"C:\Фото\b.jpg", @"C:\Фото\Private\Approved\c.jpg", @"D:\other.jpg" };
        var rejected = new[] { @"C:\system.jpg", @"C:\Фото2\sibling.jpg", @"C:\Фото\Private\hidden.jpg" };
        var store = await CreateAsync(selected.Concat(rejected).ToArray());
        var query = new CatalogViewQuery
        {
            ExcludedFolders = new[] { @"C:\", @"C:\Фото\Private" },
            IncludedFolders = new[] { "c:/фото/", @"C:\Фото\Private\Approved" }, PageSize = 2
        };
        var expected = selected.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, await PathsAsync(store, query));
        Assert.Equal(expected.Length, await store.CountAsync(query));
        Assert.Equal(expected.Length, (await store.QueryGroupsAsync(query)).Sum(group => group.Count));
        foreach (var variant in new[]
        {
            query with { ViewMode = "Favorites" }, query with { ViewMode = "Recent" },
            query with { DuplicateCandidatesOnly = true }, query with { MetadataDueAtUtc = DateTime.UtcNow }
        }) Assert.Equal(expected, await PathsAsync(store, variant));
        foreach (var path in rejected) Assert.Null(await store.IndexOfAsync(query, path));
        var ordered = (await store.QueryPageAsync(query with { PageSize = 100 })).Items;
        Assert.Equal(expected.Length, (await store.QueryRangeAsync(query, ordered[0].Path, ordered[^1].Path)).Count);
        Assert.Empty(await store.QueryRangeAsync(query, rejected[0], ordered[^1].Path));
        // Explorer navigation can inspect an excluded folder without including it in the library.
        Assert.Equal(6, await store.CountAsync(query with { ViewMode = "Folder", Folder = @"C:\" }));
    }

    [Theory]
    [InlineData(@"D:\Фото", "d:/фото/", @"D:\Фото\x.jpg", @"D:\Фото2\y.jpg")]
    [InlineData(@"\\Server\Share", "//server/share/", @"\\SERVER\SHARE\x.jpg", @"\\Server\Share2\y.jpg")]
    [InlineData(@"D:\A%_B", "d:/a%_b/", @"D:\A%_B\x.jpg", @"D:\AXXB\y.jpg")]
    public async Task EqualDepthExclusionWinsWithoutMatchingAdjacentNamesOrTreatingWildcardsSpecially(
        string excluded, string included, string excludedFile, string neighborFile)
    {
        var store = await CreateAsync(excludedFile, neighborFile);
        var query = new CatalogViewQuery { ExcludedFolders = new[] { excluded }, IncludedFolders = new[] { included } };
        Assert.Equal(new[] { neighborFile }, await PathsAsync(store, query));
    }

    [Fact]
    public async Task ManyTreeOverridesDoNotExhaustSqlExpressionOrParameterLimits()
    {
        var paths = Enumerable.Range(0, 50_000).Select(i => $"D:/Root/{i % 2000:D4}/image-{i:D6}.jpg")
            .Append(@"D:\Root\0000\Selected\a.jpg").Append(@"D:\Elsewhere\c.jpg").ToArray();
        var store = await CreateAsync(paths);
        var query = new CatalogViewQuery
        {
            ExcludedFolders = Enumerable.Range(0, 1500).Select(i => $"D:/Root/{i:D4}").ToArray(),
            IncludedFolders = new[] { @"D:\Root\0000\Selected" }
        };
        Assert.Equal(12_502, await store.CountAsync(query));
        Assert.Equal(12_502, (await store.QueryGroupsAsync(query)).Sum(group => group.Count));
        var first = await store.QueryPageAsync(query);
        var second = await store.QueryPageAsync(query with { Cursor = first.NextCursor });
        Assert.Equal(query.PageSize, first.Items.Count); Assert.Equal(query.PageSize, second.Items.Count);
        Assert.Empty(first.Items.Select(item => item.Path).Intersect(second.Items.Select(item => item.Path)));
        Assert.All(first.Items.Concat(second.Items), item => Assert.True(IncludedByReference(item.Path, query)));

        // Exercise the production SQL builder against the actual schema. A wall-clock assertion would
        // be flaky on CI; the index plan proves we do not rescan all media or JSON rules per media row.
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "catalog-v2.sqlite")};Pooling=False");
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        var builder = typeof(SqliteDesktopCatalogStore).GetMethod("BuildFrom", BindingFlags.NonPublic | BindingFlags.Static)!;
        var from = (string)builder.Invoke(null, new object[] { query, command, CancellationToken.None })!;
        command.CommandText = $"EXPLAIN QUERY PLAN SELECT COUNT(*) FROM {from};";
        var plan = new List<string>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, detail => detail.Contains("SEARCH paths", StringComparison.Ordinal)
            && detail.Contains("path_key>? AND path_key<?", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, detail => detail.Contains("CORRELATED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IntervalCompilationMatchesLongestRulesAcrossDeepOverridesAndUnicodeBoundaries()
    {
        // Supplementary Unicode sorts before private-use BMP in UTF-16, but after it in SQLite UTF-8.
        var roots = new[] { "D:/A", "D:/A0", "D:/A%_", "D:/\uE000", "D:/😀", "D:/Z" };
        var paths = roots.SelectMany(root => Enumerable.Range(0, 12)
            .SelectMany(i => new[] { $"{root}/{i:D2}/a.jpg", $"{root}/{i:D2}/Keep/b.jpg", $"{root}/{i:D2}/Keep/Private/c.jpg" }))
            .Append("D:/Outside/d.jpg").ToArray();
        var store = await CreateAsync(paths);
        var excluded = roots.SelectMany(root => Enumerable.Range(0, 12)
            .SelectMany(i => new[] { $"{root}/{i:D2}", $"{root}/{i:D2}/Keep/Private" })).Reverse().ToArray();
        var query = new CatalogViewQuery
        {
            ExcludedFolders = excluded,
            IncludedFolders = roots.SelectMany(root => Enumerable.Range(0, 12).Select(i => $"{root}/{i:D2}/Keep")).ToArray(),
            PageSize = 17
        };
        var expected = paths.Where(path => IncludedByReference(path, query)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, await PathsAsync(store, query));
        Assert.Equal(expected.Length, await store.CountAsync(query));
        Assert.Equal(expected.Length, (await store.QueryGroupsAsync(query)).Sum(group => group.Count));
        Assert.Equal(expected, await PathsAsync(store, query with { NewestFirst = false }));
    }

    private static bool IncludedByReference(string path, CatalogViewQuery query)
    {
        static string Key(string value) => value.Replace('\\', '/').TrimEnd('/').ToUpperInvariant();
        var key = Key(path);
        int Match(IEnumerable<string> rules, int fallback) => rules.Select(Key)
            .Where(rule => key.StartsWith(rule + "/", StringComparison.Ordinal)).Select(rule => rule.Length)
            .DefaultIfEmpty(fallback).Max();
        return Match(query.IncludedFolders, -1) > Match(query.ExcludedFolders, -2);
    }

    [Fact]
    public async Task ChangingPositiveOverridesInvalidatesPagingCursor()
    {
        var store = await CreateAsync(@"C:\Photos\a.jpg", @"C:\Photos\b.jpg", @"C:\Other\c.jpg");
        var query = new CatalogViewQuery { ExcludedFolders = new[] { @"C:\" }, IncludedFolders = new[] { @"C:\Photos" }, PageSize = 1 };
        var first = await store.QueryPageAsync(query); Assert.NotNull(first.NextCursor);
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryPageAsync(query with
        {
            IncludedFolders = new[] { @"C:\Photos", @"C:\Other" }, Cursor = first.NextCursor
        }));
    }

    [Fact]
    public async Task PositiveOverridesAreAdditiveSettingsAndNeverDiscardLegacyExclusions()
    {
        var store = await CreateAsync();
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "catalog-v2.sqlite")};Pooling=False"))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO desktop_excluded_folders(path) VALUES('C:\');
                INSERT INTO desktop_settings(key,value) VALUES('view_mode','All'),('future_setting','preserve-me');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var legacy = await store.LoadAsync(includeItems: false);
        Assert.Equal(new[] { @"C:\" }, legacy.ExcludedFolders); Assert.Empty(legacy.IncludedFolders);
        legacy.IncludedFolders.Add(@"C:\Фото");
        await store.SaveAsync(legacy, saveItems: false);
        // Reinitialization is non-destructive and requires no media/schema rewrite for this new JSON key.
        var reopened = new SqliteDesktopCatalogStore(_root); await reopened.InitializeAsync();
        var restored = await reopened.LoadAsync(includeItems: false);
        Assert.Equal(legacy.ExcludedFolders, restored.ExcludedFolders);
        Assert.Equal(legacy.IncludedFolders, restored.IncludedFolders);
        await using var check = new SqliteConnection($"Data Source={Path.Combine(_root, "catalog-v2.sqlite")};Pooling=False");
        await check.OpenAsync(); await using var query = check.CreateCommand();
        query.CommandText = "SELECT value FROM desktop_settings WHERE key='future_setting';";
        Assert.Equal("preserve-me", await query.ExecuteScalarAsync());
        query.CommandText = "SELECT value FROM desktop_settings WHERE key='included_folders';";
        Assert.Equal(legacy.IncludedFolders, JsonSerializer.Deserialize<List<string>>((string)(await query.ExecuteScalarAsync())!));
    }

    [Fact]
    public void LegacyJsonWithoutPositiveOverridesRetainsOriginalExclusions()
    {
        var state = JsonSerializer.Deserialize<LocalCatalogState>("""{"ExcludedFolders":["C:\\"]}""")!;
        Assert.Equal(new[] { @"C:\" }, state.ExcludedFolders); Assert.Empty(state.IncludedFolders);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
