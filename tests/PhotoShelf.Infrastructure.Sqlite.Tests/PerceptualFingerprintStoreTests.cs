using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Duplicates;
using PhotoShelf.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class PerceptualFingerprintStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-fingerprint-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StaleObservedResultIsRejectedAndCurrentResultIsQueryable()
    {
        var catalog = new SqliteDesktopCatalogStore(_root);
        await catalog.InitializeAsync();
        var path = Path.Combine(_root, "image.png");
        var observed = await SeedAsync(catalog, new SavedMediaItem
        {
            AssetId = "asset-1", Path = path, SizeBytes = 100,
            FileModifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local),
            ObservationVersion = 7, Availability = FileAvailability.Available
        });
        var store = new PerceptualFingerprintStore(_root);
        await store.InitializeAsync();
        var fingerprint = new PerceptualFingerprint(1, 0x1234, 0x5678, 32, 24);

        var stale = Clone(observed); stale.ObservationVersion--;
        var rejected = await store.SaveObservedBatchAsync([new(stale, PerceptualFingerprintReadResult.Found(fingerprint), DateTime.UtcNow)]);
        Assert.False(Assert.Single(rejected));

        var accepted = await store.SaveObservedBatchAsync([new(observed, PerceptualFingerprintReadResult.Found(fingerprint), DateTime.UtcNow)]);
        Assert.True(Assert.Single(accepted));
        var candidates = await store.FindCandidatesAsync(fingerprint);
        Assert.Equal(path, Assert.Single(candidates).Path);
    }

    [Fact]
    public async Task TerminalOutcomeLeavesQueueWhileTransientWaitsUntilDue()
    {
        var catalog = new SqliteDesktopCatalogStore(_root);
        await catalog.InitializeAsync();
        var first = await SeedAsync(catalog, Item("a", Path.Combine(_root, "a.png")));
        var second = await SeedAsync(catalog, Item("b", Path.Combine(_root, "b.png")));
        var store = new PerceptualFingerprintStore(_root);
        await store.InitializeAsync();
        var now = DateTime.UtcNow;
        var committed = await store.SaveObservedBatchAsync([
            new(first, PerceptualFingerprintReadResult.Unsupported("codec"), now),
            new(second, PerceptualFingerprintReadResult.Transient("locked", now.AddHours(1)), now)]);
        Assert.All(committed, Assert.True);

        var scope = new PerceptualFingerprintScope([], [], true, now);
        Assert.Empty(await store.QueryDuePageAsync(scope));
        var due = await store.QueryDuePageAsync(scope with { DueAtUtc = now.AddHours(2) });
        Assert.Equal(second.Path, Assert.Single(due).Path);
    }

    [Fact]
    public async Task BandLookupVerifiesHammingDistanceAndExcludesRequestedAsset()
    {
        var catalog = new SqliteDesktopCatalogStore(_root);
        await catalog.InitializeAsync();
        var first = await SeedAsync(catalog, Item("a", Path.Combine(_root, "a.png")));
        var second = await SeedAsync(catalog, Item("b", Path.Combine(_root, "b.png")));
        var store = new PerceptualFingerprintStore(_root);
        await store.InitializeAsync();
        var baseline = new PerceptualFingerprint(1, 0, 0, 20, 20);
        var close = new PerceptualFingerprint(1, 1UL << 18, 1UL << 8, 20, 20);
        Assert.All(await store.SaveObservedBatchAsync([
            new(first, PerceptualFingerprintReadResult.Found(baseline), DateTime.UtcNow),
            new(second, PerceptualFingerprintReadResult.Found(close), DateTime.UtcNow)]), Assert.True);

        var result = await store.FindCandidatesAsync(baseline, exceptAssetId: first.AssetId, maximumDifferenceDistance: 1);
        var match = Assert.Single(result);
        Assert.Equal(second.AssetId, match.AssetId);
        Assert.Equal(1, match.DifferenceDistance);
    }

    [Fact]
    public async Task ObservedBatchReturnsOnlyCurrentMatchingSnapshots()
    {
        var catalog = new SqliteDesktopCatalogStore(_root);
        await catalog.InitializeAsync();
        var first = await SeedAsync(catalog, Item("a", Path.Combine(_root, "a.png")));
        var second = await SeedAsync(catalog, Item("b", Path.Combine(_root, "b.png")));
        var store = new PerceptualFingerprintStore(_root);
        await store.InitializeAsync();
        var fingerprint = new PerceptualFingerprint(1, 0x12, 0x34, 32, 24);
        Assert.All(await store.SaveObservedBatchAsync([
            new(first, PerceptualFingerprintReadResult.Found(fingerprint), DateTime.UtcNow),
            new(second, PerceptualFingerprintReadResult.Found(fingerprint), DateTime.UtcNow)]), Assert.True);

        var stale = Clone(second);
        stale.ObservationVersion++;
        var result = await store.ReadObservedBatchAsync([first, stale]);

        var observed = Assert.Single(result);
        Assert.Equal(first.AssetId, observed.Item.AssetId);
        Assert.Equal(fingerprint, observed.Fingerprint);
    }

    [Fact]
    public async Task QueueHonorsFolderExceptionsAndSystemVisibility()
    {
        var catalog = new SqliteDesktopCatalogStore(_root);
        await catalog.InitializeAsync();
        var excludedRoot = Path.Combine(_root, "excluded");
        var includedChild = Path.Combine(excludedRoot, "keep");
        var excluded = Item("excluded", Path.Combine(excludedRoot, "skip.png"));
        var included = Item("included", Path.Combine(includedChild, "keep.png"));
        var system = Item("system", Path.Combine(_root, "system.png")); system.IsHiddenOrSystem = true;
        await catalog.UpsertItemsAsync([excluded, included, system]);
        var store = new PerceptualFingerprintStore(_root);
        await store.InitializeAsync();

        var visible = await store.QueryDuePageAsync(new([includedChild], [excludedRoot], false, DateTime.UtcNow));
        Assert.Equal([included.Path], visible.Select(item => item.Path));

        var withSystem = await store.QueryDuePageAsync(new([includedChild], [excludedRoot], true, DateTime.UtcNow));
        Assert.Equal([included.Path, system.Path], withSystem.Select(item => item.Path).Order(StringComparer.OrdinalIgnoreCase));
    }

    private static SavedMediaItem Item(string id, string path) => new()
    {
        AssetId = id, Path = path, SizeBytes = 10,
        FileModifiedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local),
        ObservationVersion = 1, Availability = FileAvailability.Available
    };

    private static async Task<SavedMediaItem> SeedAsync(SqliteDesktopCatalogStore catalog, SavedMediaItem item)
    {
        await catalog.UpsertItemsAsync([item]);
        return (await catalog.GetItemAsync(item.Path))!;
    }

    private static SavedMediaItem Clone(SavedMediaItem item) => new()
    {
        AssetId = item.AssetId, Path = item.Path, SizeBytes = item.SizeBytes,
        FileModifiedAt = item.FileModifiedAt, ObservationVersion = item.ObservationVersion,
        Availability = item.Availability
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
