using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class CatalogRangeSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-range-" + Guid.NewGuid().ToString("N"));
    private async Task<SqliteDesktopCatalogStore> CreateAsync(SavedMediaItem[] items)
    {
        var store = new SqliteDesktopCatalogStore(_root);
        await store.InitializeAsync(); await store.UpsertItemsAsync(items);
        return store;
    }
    private static SavedMediaItem Item(int index, int generation = 0) => new()
    {
        Path = $@"C:\PhotoShelf-test-only\{index:D4}.jpg", SizeBytes = generation + 1,
        FileModifiedAt = new DateTime(2025, 1, 1).AddYears(generation).AddMinutes(index), IsFavorite = index % 2 == 0
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RangeUsesCurrentPathsAndOrderAfterEarlierRowsWereInserted(bool newestFirst)
    {
        var store = await CreateAsync(Enumerable.Range(10, 10).Select(i => Item(i)).ToArray());
        var query = new CatalogViewQuery { NewestFirst = newestFirst };
        await store.UpsertItemsAsync(Enumerable.Range(0, 10).Select(i => Item(i)).ToArray());
        var range = await store.QueryRangeAsync(query, Item(16).Path, Item(12).Path);
        var expected = Enumerable.Range(12, 5);
        if (newestFirst) expected = expected.Reverse();
        Assert.Equal(expected.Select(i => Item(i).Path), range.Select(item => item.Path));
    }

    [Fact]
    public async Task MissingOrFilteredOutEndpointSelectsNothing()
    {
        var store = await CreateAsync(Enumerable.Range(0, 10).Select(i => Item(i)).ToArray());
        var favorites = new CatalogViewQuery { ViewMode = "Favorites", NewestFirst = false };
        Assert.Empty(await store.QueryRangeAsync(favorites, Item(0).Path, Item(5).Path));
        Assert.Empty(await store.QueryRangeAsync(new(), Item(0).Path, "missing.jpg"));
        Assert.Equal(new[] { Item(2).Path, Item(4).Path, Item(6).Path },
            (await store.QueryRangeAsync(favorites, Item(6).Path, Item(2).Path)).Select(item => item.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EqualDateUnicodeEndpointsUseSameCollationAsVisiblePages(bool newestFirst)
    {
        var names = new[] { "a", "ёлка", "\ue000", "😀", "🦒" };
        var items = names.Select(name => new SavedMediaItem
        {
            Path = $@"C:\PhotoShelf-test-only\{name}.jpg", SizeBytes = 1, FileModifiedAt = new DateTime(2025, 1, 1)
        }).ToArray();
        var store = await CreateAsync(items);
        var query = new CatalogViewQuery { NewestFirst = newestFirst };
        var ordered = (await store.QueryPageAsync(query)).Items;
        var expected = ordered.Skip(2).Take(3).Select(item => item.Path);
        Assert.Equal(expected, (await store.QueryRangeAsync(query, items[4].Path, items[2].Path)).Select(item => item.Path));
    }

    [Fact]
    public async Task ConcurrentCatalogRewriteCannotMixEndpointAndRangeSnapshots()
    {
        var store = await CreateAsync(Enumerable.Range(0, 100).Select(i => Item(i)).ToArray());
        var writer = Task.Run(async () =>
        {
            for (var generation = 1; generation <= 20; generation++)
                await store.UpsertItemsAsync(Enumerable.Range(0, 100).Select(i => Item(i, generation)).ToArray());
        });
        try
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var range = await store.QueryRangeAsync(new() { NewestFirst = false }, Item(20).Path, Item(70).Path);
                Assert.Equal(Enumerable.Range(20, 51).Select(i => Item(i).Path), range.Select(item => item.Path));
                Assert.Single(range.Select(item => item.SizeBytes).Distinct());
            }
        }
        finally { await writer; }
    }

    [Fact]
    public async Task PreCanceledRangeDoesNotInterfereWithLaterWrites()
    {
        var store = await CreateAsync(Enumerable.Range(0, 100).Select(i => Item(i)).ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.QueryRangeAsync(new(), Item(0).Path, Item(99).Path, cancellation.Token));
        await store.SetFavoriteAsync(Item(1).Path, true);
        Assert.True((await store.GetItemAsync(Item(1).Path))!.IsFavorite);
        Assert.Equal(100, (await store.QueryRangeAsync(new(), Item(0).Path, Item(99).Path)).Count);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
