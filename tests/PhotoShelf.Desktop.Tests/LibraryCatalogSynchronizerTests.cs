using System.IO;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Desktop;
using PhotoShelf.Infrastructure.Sqlite;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

// Service-level integration with an injected read-only filesystem probe; all databases are synthetic.
public sealed class LibraryCatalogSynchronizerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "photoshelf-sync-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Modified = new(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Capture = new(2020, 1, 2);
    private static readonly DateTime Checked = new(2026, 9, 28, 11, 0, 0, DateTimeKind.Utc);
    private string Source => Path.Combine(_directory, "photos", "source.jpg");
    private string Destination => Path.Combine(_directory, "photos", "renamed.jpg");
    private const string Identity = "synthetic-volume:file-id:creation-time";

    private async Task<SqliteDesktopCatalogStore> CreateAsync()
    {
        var store = new SqliteDesktopCatalogStore(Path.Combine(_directory, "catalog")); await store.InitializeAsync();
        await store.UpsertItemsAsync([new SavedMediaItem
        {
            Path = Source, FileIdentity = Identity, SizeBytes = 42, FileModifiedAt = Modified,
            CaptureDate = Capture, MetadataIndexed = true, IsFavorite = true,
            Availability = FileAvailability.Available, AvailabilityCheckedAtUtc = Checked
        }]);
        return store;
    }
    private static FileSystemProbeResult Available(string path, string? identity = Identity) =>
        new(path, FileAvailability.Available, Checked.AddMinutes(1), 42, Modified, FileIdentity: identity);
    private static FileSystemProbeResult Missing(string path) => new(path, FileAvailability.Missing,
        Checked.AddMinutes(1), ErrorCode: "FileNotFound");

    [Fact]
    public async Task TemporaryNonrecursiveBrowseDoesNotImportDescendantsUnderDefaultInclusionRules()
    {
        var store = new SqliteDesktopCatalogStore(Path.Combine(_directory, "catalog")); await store.InitializeAsync();
        var root = Path.Combine(_directory, "browse"); var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);
        var direct = Path.Combine(root, "direct.jpg"); var nested = Path.Combine(child, "nested.jpg");
        await File.WriteAllBytesAsync(direct, [1]); await File.WriteAllBytesAsync(nested, [2]);
        var synchronizer = new LibraryCatalogSynchronizer(store, _ => Task.CompletedTask,
            new Probe(path => Available(path, path), path => Available(path) with { IsDirectory = true }));
        await synchronizer.ProcessAsync(new(1, [], [root], []), new FolderInclusionRules([]), root,
            browseRecursively: false, includeSystem: true, CancellationToken.None);
        Assert.NotNull(await store.GetItemAsync(direct)); Assert.Null(await store.GetItemAsync(nested));
    }

    [Fact]
    public async Task NonrecursiveBrowseDoesNotProbeExistingFilesInNestedLibraryRoots()
    {
        var store = new SqliteDesktopCatalogStore(Path.Combine(_directory, "catalog")); await store.InitializeAsync();
        var root = Path.Combine(_directory, "browse"); var child = Path.Combine(root, "included-child");
        Directory.CreateDirectory(child);
        var nested = Enumerable.Range(0, 600).Select(i => new SavedMediaItem
        {
            Path = Path.Combine(child, $"{i:D4}.jpg"), SizeBytes = 42, FileModifiedAt = Modified,
            Availability = FileAvailability.Available, AvailabilityCheckedAtUtc = Checked, FileIdentity = $"nested-{i}"
        }).ToArray();
        await store.UpsertItemsAsync(nested);
        var probes = new List<string>(); var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store, update => { updates.Add(update); return Task.CompletedTask; },
            new Probe(path => { probes.Add(path); return Missing(path); }, path => Available(path) with { IsDirectory = true }));
        await synchronizer.ProcessAsync(new(1, [], [root], []), new FolderInclusionRules([]), root,
            browseRecursively: false, includeSystem: true, CancellationToken.None, libraryRoots: [child]);
        Assert.Empty(probes);
        Assert.False(Assert.Single(updates, update => update.Completed).CatalogChanged);
        Assert.Equal(600, await store.CountAsync(new() { IncludeSystemFolders = true }));
        Assert.Equal(FileAvailability.Available, (await store.GetItemAsync(nested[0].Path))!.Availability);

        updates.Clear();
        await synchronizer.ProcessAsync(new(2, [], [child], []), new FolderInclusionRules([]), root,
            browseRecursively: false, includeSystem: true, CancellationToken.None, libraryRoots: [child]);
        Assert.Equal(600, probes.Count);
        Assert.True(Assert.Single(updates, update => update.Completed).CatalogChanged);
        Assert.Equal(FileAvailability.Missing, (await store.GetItemAsync(nested[0].Path))!.Availability);
    }

    [Fact]
    public async Task RootStateAndUnsupportedEventsCompleteWithoutCatalogChangesOrFileProbes()
    {
        var store = await CreateAsync(); var root = Path.GetDirectoryName(Source)!;
        var before = (await store.GetItemAsync(Source))!; var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store, update => { updates.Add(update); return Task.CompletedTask; },
            new Probe(_ => throw new InvalidOperationException("No media path needs probing.")));
        for (var generation = 1; generation <= 3; generation++)
            await synchronizer.ProcessAsync(new(generation, [Path.Combine(root, "activity.log")], [],
                [new(root, FileAvailability.AccessDenied, false, "AccessDenied")]), new FolderInclusionRules([]),
                root, false, true, CancellationToken.None, libraryRoots: [root]);
        Assert.Equal(3, updates.Count(update => update.Completed));
        Assert.All(updates, update => { Assert.False(update.CatalogChanged); Assert.Empty(update.Items); });
        Assert.Equal(before.ObservationVersion, (await store.GetItemAsync(Source))!.ObservationVersion);
    }

    [Fact]
    public async Task StableReconciliationDoesNotAdvanceObservationOrReportCatalogChanges()
    {
        var store = await CreateAsync(); var root = Path.GetDirectoryName(Source)!;
        var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store, update => { updates.Add(update); return Task.CompletedTask; },
            new Probe(path => Available(path), path => Available(path) with { IsDirectory = true }));
        await synchronizer.ObservePathsAsync([Source], false, CancellationToken.None);
        var before = (await store.GetItemAsync(Source))!; updates.Clear();
        for (var generation = 1; generation <= 2; generation++)
            await synchronizer.ProcessAsync(new(generation, [], [root], []), new FolderInclusionRules([]), root,
                false, true, CancellationToken.None, libraryRoots: [root]);
        Assert.Equal(2, updates.Count(update => update.Completed));
        Assert.All(updates, update => { Assert.False(update.CatalogChanged); Assert.Empty(update.Items); });
        Assert.Equal(before.ObservationVersion, (await store.GetItemAsync(Source))!.ObservationVersion);
        Assert.True((await store.GetItemAsync(Source))!.MetadataIndexed);
    }

    [Fact]
    public async Task CompletionTracksOnlyItsOwnCommittedWritesWhenAnotherInvocationPublishes()
    {
        var store = await CreateAsync(); var root = Path.GetDirectoryName(Source)!;
        var updates = new List<LibraryCatalogUpdate>();
        LibraryCatalogSynchronizer? synchronizer = null;
        synchronizer = new LibraryCatalogSynchronizer(store, async update =>
        {
            updates.Add(update);
            if (update.Items.Count > 0)
                await synchronizer!.ProcessAsync(new(2, [], [], []), new FolderInclusionRules([]), root,
                    false, true, CancellationToken.None, libraryRoots: [root]);
        }, new Probe(path => Missing(path)));
        await synchronizer.ProcessAsync(new(1, [Source], [], []), new FolderInclusionRules([]), root,
            false, true, CancellationToken.None, libraryRoots: [root]);
        Assert.True(Assert.Single(updates, update => update.Items.Count > 0).CatalogChanged);
        Assert.Equal(new[] { false, true }, updates.Where(update => update.Completed).Select(update => update.CatalogChanged));
    }

    [Fact]
    public async Task CancelledNoOpProcessStillCompletesWithoutClaimingCatalogChanges()
    {
        var store = await CreateAsync(); var root = Path.GetDirectoryName(Source)!;
        var before = (await store.GetItemAsync(Source))!; var updates = new List<LibraryCatalogUpdate>();
        using var cancellation = new CancellationTokenSource();
        var synchronizer = new LibraryCatalogSynchronizer(store, update =>
        {
            updates.Add(update);
            if (!update.Completed) cancellation.Cancel();
            return Task.CompletedTask;
        }, new Probe(_ => throw new InvalidOperationException("No media observation was requested.")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synchronizer.ProcessAsync(
            new(1, [], [], [new(root, FileAvailability.Available, true)]), new FolderInclusionRules([]),
            root, false, true, cancellation.Token, libraryRoots: [root]));
        Assert.False(Assert.Single(updates, update => update.Completed).CatalogChanged);
        Assert.All(updates, update => Assert.Empty(update.Items));
        Assert.Equal(before.ObservationVersion, (await store.GetItemAsync(Source))!.ObservationVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledProcessPublishesCommittedBoundaryEvenBeforeObserverReturns(bool interruptPublication)
    {
        var store = await CreateAsync(); var root = Path.GetDirectoryName(Source)!;
        var updates = new List<LibraryCatalogUpdate>(); using var cancellation = new CancellationTokenSource();
        var synchronizer = new LibraryCatalogSynchronizer(store, update =>
        {
            updates.Add(update);
            if (update.Items.Count > 0)
            {
                cancellation.Cancel();
                if (interruptPublication) cancellation.Token.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        }, new Probe(path => Missing(path)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synchronizer.ProcessAsync(
            new(1, [Source], [], []), new FolderInclusionRules([]), root, false, true,
            cancellation.Token, libraryRoots: [root]));
        Assert.Equal(FileAvailability.Missing, (await store.GetItemAsync(Source))!.Availability);
        Assert.True(Assert.Single(updates, update => update.Items.Count > 0).CatalogChanged);
        Assert.True(Assert.Single(updates, update => update.Completed).CatalogChanged);

        // The replay sees an equivalent unavailable observation. The original completion
        // must already have reported the commit; this no-op cannot repair a missing boundary.
        updates.Clear();
        await synchronizer.ProcessAsync(new(2, [Source], [], []), new FolderInclusionRules([]),
            root, false, true, CancellationToken.None, libraryRoots: [root]);
        Assert.False(Assert.Single(updates, update => update.Completed).CatalogChanged);
        Assert.All(updates, update => Assert.Empty(update.Items));
    }

    [Fact]
    public async Task PersistentExcludedAncestorStillDiscoversFilesInExplicitlyIncludedChild()
    {
        var store = new SqliteDesktopCatalogStore(Path.Combine(_directory, "catalog")); await store.InitializeAsync();
        var root = Path.Combine(_directory, "library"); var child = Path.Combine(root, "included-child");
        Directory.CreateDirectory(child);
        var excluded = Path.Combine(root, "excluded.jpg"); var included = Path.Combine(child, "included.jpg");
        await File.WriteAllBytesAsync(excluded, [1]); await File.WriteAllBytesAsync(included, [2]);
        var synchronizer = new LibraryCatalogSynchronizer(store, _ => Task.CompletedTask,
            new Probe(path => Available(path, path), path => Available(path) with { IsDirectory = true }));
        await synchronizer.ProcessAsync(new(1, [], [root], []), new FolderInclusionRules([root], [child]), null,
            browseRecursively: false, includeSystem: true, CancellationToken.None, libraryRoots: [root]);
        Assert.Null(await store.GetItemAsync(excluded)); Assert.NotNull(await store.GetItemAsync(included));
    }

    [Fact]
    public async Task ForcedRenameRevalidationInvalidatesSameFingerprintHashAndMetadataWithoutLosingFavorite()
    {
        var store = await CreateAsync(); var original = (await store.GetItemAsync(Source))!;
        var hashes = new DuplicateHashStore(Path.Combine(_directory, "catalog")); await hashes.InitializeAsync();
        Assert.True(await hashes.SaveObservedAsync(new SavedDuplicateHash { Path = Source, SizeBytes = 42,
            FileModifiedAt = original.FileModifiedAt, Hash = new string('A', 64) }, original));
        var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store, update => { updates.Add(update); return Task.CompletedTask; },
            new Probe(path => path == Source ? Missing(path) : Available(path)));
        await synchronizer.ObservePathsAsync([Destination], forceContent: true, CancellationToken.None);
        var moved = (await store.GetItemAsync(Destination))!;
        Assert.Equal(original.AssetId, moved.AssetId); Assert.True(moved.IsFavorite);
        Assert.False(moved.MetadataIndexed); Assert.Equal(PhotoShelf.Application.Metadata.MetadataReadStatus.Pending, moved.MetadataStatus);
        Assert.Equal(Capture, moved.CaptureDate); // forced verification is not an authoritative absent-date result
        Assert.Null(await hashes.TryGetAsync(Destination, 42, moved.FileModifiedAt));
        Assert.Null(await hashes.TryGetAsync(Source, 42, original.FileModifiedAt));
        Assert.True(moved.ObservationVersion > original.ObservationVersion);
        Assert.Equal(moved.ObservationVersion, Assert.Single(updates.SelectMany(update => update.Renames)).Destination.ObservationVersion);
    }

    [Fact]
    public async Task MissingAndReappearedFilesPreserveAssetFavoriteAndCaptureDate()
    {
        var store = await CreateAsync(); var original = (await store.GetItemAsync(Source))!;
        var state = Missing(Source); var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store, update => { updates.Add(update); return Task.CompletedTask; },
            new Probe(_ => state));
        await synchronizer.ObservePathsAsync([Source], false, CancellationToken.None);
        var missing = (await store.GetItemAsync(Source))!;
        Assert.Equal(FileAvailability.Missing, missing.Availability); Assert.Equal(Capture, missing.CaptureDate);
        Assert.True(missing.IsFavorite); Assert.Equal(original.AssetId, missing.AssetId);
        state = Available(Source) with { CheckedAtUtc = Checked.AddMinutes(2) };
        await synchronizer.ObservePathsAsync([Source], false, CancellationToken.None);
        var restored = (await store.GetItemAsync(Source))!;
        Assert.Equal(FileAvailability.Available, restored.Availability); Assert.True(restored.IsFavorite);
        Assert.Equal(Capture, restored.CaptureDate); Assert.Equal(original.AssetId, restored.AssetId);
        Assert.Collection(updates, first => Assert.Equal(FileAvailability.Missing, Assert.Single(first.Items).Availability),
            second => Assert.Equal(FileAvailability.Available, Assert.Single(second.Items).Availability));
    }

    [Theory]
    [InlineData(FileAvailability.AccessDenied, "AccessDenied")]
    [InlineData(FileAvailability.RootOffline, "Disconnected")]
    [InlineData(FileAvailability.NeedsVerification, "PhysicalPathChanged")]
    public async Task PerFileRecoveryRevalidatesSameFingerprintAfterExplicitProbeFailure(FileAvailability failure, string error)
    {
        var store = await CreateAsync(); var original = (await store.GetItemAsync(Source))!;
        var hashes = new DuplicateHashStore(Path.Combine(_directory, "catalog")); await hashes.InitializeAsync();
        Assert.True(await hashes.SaveObservedAsync(new SavedDuplicateHash { Path = Source, SizeBytes = 42,
            FileModifiedAt = original.FileModifiedAt, Hash = new string('B', 64) }, original));
        var state = new FileSystemProbeResult(Source, failure, Checked.AddMinutes(1), ErrorCode: error);
        var synchronizer = new LibraryCatalogSynchronizer(store, _ => Task.CompletedTask, new Probe(_ => state));
        await synchronizer.ObservePathsAsync([Source], forceContent: true, CancellationToken.None);
        var inaccessible = (await store.GetItemAsync(Source))!;
        Assert.Equal(failure, inaccessible.Availability); Assert.True(inaccessible.MetadataIndexed);
        Assert.Equal(Capture, inaccessible.CaptureDate); Assert.True(inaccessible.IsFavorite);
        state = Available(Source) with { CheckedAtUtc = Checked.AddMinutes(2) };
        // Recovery is a normal periodic observation: the earlier change notification must not be forgotten.
        await synchronizer.ObservePathsAsync([Source], forceContent: false, CancellationToken.None);
        var recovered = (await store.GetItemAsync(Source))!;
        Assert.Equal(FileAvailability.Available, recovered.Availability); Assert.True(recovered.IsFavorite);
        Assert.Equal(original.AssetId, recovered.AssetId); Assert.Equal(Capture, recovered.CaptureDate);
        Assert.False(recovered.MetadataIndexed);
        Assert.Equal(PhotoShelf.Application.Metadata.MetadataReadStatus.Pending, recovered.MetadataStatus);
        Assert.Null(await hashes.TryGetAsync(Source, 42, recovered.FileModifiedAt));
    }

    [Fact]
    public async Task CommittedRenameIsPublishedWhenCancellationInterruptsLaterPathInSameBatch()
    {
        var store = await CreateAsync(); var original = (await store.GetItemAsync(Source))!;
        using var cancellation = new CancellationTokenSource();
        var later = Path.Combine(_directory, "photos", "later.jpg");
        var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store, update => { updates.Add(update); return Task.CompletedTask; },
            new Probe(path =>
            {
                if (path == Source) return Missing(path);
                if (path == later) { cancellation.Cancel(); return Available(path, "other-file"); }
                return Available(path);
            }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synchronizer.ObservePathsAsync([Destination, later], false, cancellation.Token));
        Assert.Null(await store.GetItemAsync(Source));
        Assert.Equal(original.AssetId, (await store.GetItemAsync(Destination))!.AssetId);
        var rename = Assert.Single(updates.SelectMany(update => update.Renames));
        Assert.Equal(Source, rename.Source); Assert.Equal(Destination, rename.Destination.Path);
        Assert.Equal(original.AssetId, rename.Destination.AssetId);
    }

    [Fact]
    public async Task IdentityCollisionDoesNotStealEitherExistingAssetsFavoriteOrPath()
    {
        var store = await CreateAsync(); var original = (await store.GetItemAsync(Source))!;
        var second = Path.Combine(_directory, "photos", "hardlink.jpg");
        await store.UpsertItemsAsync([new() { Path = second, SizeBytes = 42, FileModifiedAt = Modified,
            FileIdentity = Identity, Availability = FileAvailability.Available, AvailabilityCheckedAtUtc = Checked }]);
        var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store, update => { updates.Add(update); return Task.CompletedTask; },
            new Probe(path => path == Destination ? Available(path) : Missing(path)));
        await synchronizer.ObservePathsAsync([Destination], false, CancellationToken.None);
        Assert.Empty(updates.SelectMany(update => update.Renames));
        Assert.Equal(original.AssetId, (await store.GetItemAsync(Source))!.AssetId);
        Assert.True((await store.GetItemAsync(Source))!.IsFavorite);
        Assert.NotNull(await store.GetItemAsync(second));
        var added = (await store.GetItemAsync(Destination))!;
        Assert.NotEqual(original.AssetId, added.AssetId); Assert.False(added.IsFavorite);
    }

    [Fact]
    public async Task StillPresentSourcePreventsRenameEvenWhenIdentityAndFingerprintMatch()
    {
        var store = await CreateAsync(); var original = (await store.GetItemAsync(Source))!;
        var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store, update => { updates.Add(update); return Task.CompletedTask; },
            new Probe(path => Available(path)));
        await synchronizer.ObservePathsAsync([Destination], false, CancellationToken.None);
        Assert.Empty(updates.SelectMany(update => update.Renames));
        Assert.Equal(original.AssetId, (await store.GetItemAsync(Source))!.AssetId);
        Assert.NotEqual(original.AssetId, (await store.GetItemAsync(Destination))!.AssetId);
    }

    [Fact]
    public async Task ConfirmedQuarantineIsNotResurrectedByAWatcherObservation()
    {
        var store = await CreateAsync();
        await store.MoveItemAsync(Source, Destination, removeFromLibrary: true);
        var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store, update => { updates.Add(update); return Task.CompletedTask; },
            new Probe(path => Available(path)));
        await synchronizer.ObservePathsAsync([Destination], true, CancellationToken.None);
        Assert.Empty(updates); Assert.Equal(0, await store.CountAsync(new() { IncludeSystemFolders = true }));
        Assert.True((await store.GetItemAsync(Destination))!.IsFavorite);
    }

    private sealed class Probe(Func<string, FileSystemProbeResult> read, Func<string, FileSystemProbeResult>? root = null) : IFileSystemObservationProbe
    {
        public FileSystemProbeResult ProbeFile(string path) => read(path);
        public FileSystemProbeResult ProbeRoot(string path) => root?.Invoke(path) ??
            throw new InvalidOperationException("This test exercises individual file observations only.");
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
