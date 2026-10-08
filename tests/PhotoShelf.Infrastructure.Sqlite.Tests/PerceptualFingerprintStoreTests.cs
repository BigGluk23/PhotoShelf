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
    public async Task CandidateLookupHonorsCancellation()
    {
        var catalog = new SqliteDesktopCatalogStore(_root);
        await catalog.InitializeAsync();
        var store = new PerceptualFingerprintStore(_root);
        await store.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.FindCandidatesAsync(
            new PerceptualFingerprint(1, 0, 0, 20, 20), token: cancellation.Token));
    }

    [Fact]
    public async Task CandidateLookupInterruptsRunningSqlAndLeavesCatalogUsable()
    {
        var catalog = new SqliteDesktopCatalogStore(_root);
        await catalog.InitializeAsync();
        var item = await SeedAsync(catalog, Item("cancelled-query", Path.Combine(_root, "synthetic.png")));
        var fingerprint = new PerceptualFingerprint(1, 0, 0, 20, 20);
        using var cancellation = new CancellationTokenSource();
        using var resumeVm = new ManualResetEventSlim();
        var enteredVm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacksAfterCancellation = 0;
        var observeOnce = 0;
        var store = new PerceptualFingerprintStore(_root)
        {
            ObserveCandidateQuery = connection =>
            {
                if (Interlocked.Exchange(ref observeOnce, 1) != 0) return new Cleanup(() => { });
                var handle = connection.Handle;
                SQLitePCL.raw.sqlite3_progress_handler(handle, 1, _ =>
                {
                    if (enteredVm.TrySetResult()) resumeVm.Wait(TimeSpan.FromSeconds(10));
                    // This callback never cancels the successful case itself. A bounded watchdog
                    // makes a missing production sqlite3_interrupt fail instead of hanging the test.
                    return cancellation.IsCancellationRequested &&
                        Interlocked.Increment(ref callbacksAfterCancellation) > 256 ? 1 : 0;
                }, null);
                return new Cleanup(() => SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null, null));
            }
        };
        await store.InitializeAsync();
        Assert.True(Assert.Single(await store.SaveObservedBatchAsync([
            new(item, PerceptualFingerprintReadResult.Found(fingerprint), DateTime.UtcNow)])));

        var lookup = store.FindCandidatesAsync(fingerprint, token: cancellation.Token);
        try
        {
            await enteredVm.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(cancellation.IsCancellationRequested);
            Assert.False(lookup.IsCompleted);
            cancellation.Cancel(); // SQLite is executing the real band query at this point.
        }
        finally { resumeVm.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.InRange(callbacksAfterCancellation, 0, 32);

        var after = (await catalog.GetItemAsync(item.Path))!;
        Assert.Equal(item.AssetId, after.AssetId);
        Assert.Equal(item.ObservationVersion, after.ObservationVersion);
        Assert.Equal(item.SizeBytes, after.SizeBytes);
        Assert.Equal(item.Path, Assert.Single(await store.FindCandidatesAsync(fingerprint)).Path);
        Assert.True(Assert.Single(await store.SaveObservedBatchAsync([
            new(after, PerceptualFingerprintReadResult.Found(fingerprint), DateTime.UtcNow)])));
        await using var check = new SqliteConnection($"Data Source={Path.Combine(_root, "catalog-v2.sqlite")};Pooling=False");
        await check.OpenAsync();
        await using var command = check.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        Assert.Equal("ok", await command.ExecuteScalarAsync());
    }

    private sealed class Cleanup(Action cleanup) : IDisposable
    {
        public void Dispose() => cleanup();
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
