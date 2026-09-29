using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Desktop;
using PhotoShelf.Infrastructure.Sqlite;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

// The foreground target is a snapshot of the requested view, not an expansion of
// its recursive library owner. All original bytes and databases belong to this fixture.
public sealed class LibraryCatalogSynchronizerBrowseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "photoshelf-browse-scope-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Capture = new(2020, 1, 2);
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
    private string Root => Path.Combine(_directory, "library");

    [Fact]
    public async Task ForegroundBrowseDiscoversExcludedChildWithoutScanningItsWatchedParentOrOtherView()
    {
        var store = await CreateStoreAsync();
        var child = Path.Combine(Root, "excluded-child");
        var direct = await CreatePngAsync(Path.Combine(child, "direct.png"));
        var nested = await CreatePngAsync(Path.Combine(child, "nested", "nested.png"));
        var unrelated = await CreatePngAsync(Path.Combine(Root, "other", "original.png"));
        var unrelatedBefore = await SeedKnownAsync(store, unrelated);
        var originals = await FingerprintsAsync(direct, nested, unrelated);
        var probe = new CountingProbe();
        var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store, update => { updates.Add(update); return Task.CompletedTask; }, probe);
        var inclusion = new FolderInclusionRules([child]);

        await synchronizer.ProcessAsync(new(1, [], [], [], BrowseTarget: new(child, true)), inclusion,
            browsedFolder: Path.GetDirectoryName(unrelated), browseRecursively: true, includeSystem: true,
            CancellationToken.None, libraryRoots: [Root]);

        Assert.NotNull(await store.GetItemAsync(direct));
        Assert.NotNull(await store.GetItemAsync(nested));
        Assert.Equal([child], probe.Roots);
        Assert.NotEmpty(probe.Files);
        Assert.All(probe.Files, path => Assert.True(LibraryFolderScope.Contains(path, child, recursive: true)));
        Assert.DoesNotContain(unrelated, probe.Files);
        Assert.False(inclusion.IsIncluded(child));
        Assert.False(inclusion.IsIncluded(direct));
        await AssertKnownUnchangedAsync(store, unrelatedBefore);
        Assert.True(Assert.Single(updates, update => update.Completed).CatalogChanged);
        await AssertOriginalsUnchangedAsync(originals);
    }

    [Fact]
    public async Task NonrecursiveForegroundDoesNotProbeDescendantsDespiteRecursiveLibraryAndCurrentView()
    {
        var store = await CreateStoreAsync();
        var child = Path.Combine(Root, "selected");
        var direct = await CreatePngAsync(Path.Combine(child, "direct.png"));
        var nested = await CreatePngAsync(Path.Combine(child, "nested", "original.png"));
        var unrelated = await CreatePngAsync(Path.Combine(Root, "other", "original.png"));
        var nestedBefore = await SeedKnownAsync(store, nested);
        var unrelatedBefore = await SeedKnownAsync(store, unrelated);
        var originals = await FingerprintsAsync(direct, nested, unrelated);
        var probe = new CountingProbe();
        var synchronizer = new LibraryCatalogSynchronizer(store, _ => Task.CompletedTask, probe);

        await synchronizer.ProcessAsync(new(1, [], [], [], BrowseTarget: new(child, false)),
            new FolderInclusionRules([]), browsedFolder: Root, browseRecursively: true, includeSystem: true,
            CancellationToken.None, libraryRoots: [Root]);

        Assert.NotNull(await store.GetItemAsync(direct));
        Assert.Equal([child], probe.Roots);
        Assert.NotEmpty(probe.Files);
        Assert.All(probe.Files, path => Assert.Equal(direct, path));
        await AssertKnownUnchangedAsync(store, nestedBefore);
        await AssertKnownUnchangedAsync(store, unrelatedBefore);
        Assert.Equal(3, await store.CountAsync(new() { IncludeSystemFolders = true }));
        await AssertOriginalsUnchangedAsync(originals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterForegroundCommitPreservesOriginalsAndUnvisitedCatalogRows(bool interruptPublication)
    {
        var store = await CreateStoreAsync();
        var child = Path.Combine(Root, "selected");
        var known = await CreatePngAsync(Path.Combine(child, "known.png"));
        var newlyDiscovered = await CreatePngAsync(Path.Combine(child, "new.png"));
        var unvisited = await CreatePngAsync(Path.Combine(child, "nested", "unvisited.png"));
        var knownBefore = await SeedKnownAsync(store, known);
        var unvisitedBefore = await SeedKnownAsync(store, unvisited);
        var originals = await FingerprintsAsync(known, newlyDiscovered, unvisited);
        using var cancellation = new CancellationTokenSource();
        var probe = new CountingProbe();
        var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store, update =>
        {
            updates.Add(update);
            if (update.Items.Any(item => item.Path == newlyDiscovered))
            {
                cancellation.Cancel();
                if (interruptPublication) cancellation.Token.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        }, probe);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synchronizer.ProcessAsync(
            new(1, [], [], [], BrowseTarget: new(child, true)), new FolderInclusionRules([Root]),
            browsedFolder: null, browseRecursively: false, includeSystem: true, cancellation.Token,
            libraryRoots: [Root]));

        var committed = (await store.GetItemAsync(newlyDiscovered))!;
        Assert.NotNull(committed);
        Assert.False(string.IsNullOrEmpty(committed.AssetId));
        Assert.Equal(FileAvailability.Available, committed.Availability);
        Assert.DoesNotContain(unvisited, probe.Files);
        Assert.Equal(3, await store.CountAsync(new() { IncludeSystemFolders = true }));
        await AssertKnownUnchangedAsync(store, knownBefore);
        await AssertKnownUnchangedAsync(store, unvisitedBefore);
        Assert.Contains(updates, update => update.CatalogChanged && update.Items.Any(item => item.Path == newlyDiscovered));
        Assert.True(Assert.Single(updates, update => update.Completed).CatalogChanged);
        await AssertOriginalsUnchangedAsync(originals);
        // No cancellation result is accepted while a synthetic original is still held open.
        foreach (var path in originals.Keys)
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.True(exclusive.CanRead);
        }
    }

    private async Task<SqliteDesktopCatalogStore> CreateStoreAsync()
    {
        var store = new SqliteDesktopCatalogStore(Path.Combine(_directory, "catalog"));
        await store.InitializeAsync();
        return store;
    }

    private static async Task<string> CreatePngAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Png);
        return path;
    }

    private static async Task<SavedMediaItem> SeedKnownAsync(SqliteDesktopCatalogStore store, string path)
    {
        var item = LibraryCatalogSynchronizer.Available(new FileSystemObservationProbe().ProbeFile(path));
        item.IsFavorite = true;
        item.CaptureDate = Capture;
        item.MetadataIndexed = true;
        await store.UpsertItemsAsync([item], preserveFavorites: false);
        return (await store.GetItemAsync(path))!;
    }

    private static async Task AssertKnownUnchangedAsync(SqliteDesktopCatalogStore store, SavedMediaItem before)
    {
        var after = (await store.GetItemAsync(before.Path))!;
        Assert.NotNull(after);
        Assert.Equal(before.AssetId, after.AssetId);
        Assert.Equal(before.ObservationVersion, after.ObservationVersion);
        Assert.Equal(before.FileIdentity, after.FileIdentity);
        Assert.Equal(before.SizeBytes, after.SizeBytes);
        Assert.Equal(before.FileModifiedAt, after.FileModifiedAt);
        Assert.Equal(before.CaptureDate, after.CaptureDate);
        Assert.Equal(before.MetadataIndexed, after.MetadataIndexed);
        Assert.Equal(before.MetadataStatus, after.MetadataStatus);
        Assert.Equal(before.Availability, after.Availability);
        Assert.True(after.IsFavorite);
    }

    private sealed record Fingerprint(string Hash, long Length, DateTime ModifiedUtc);

    private static async Task<Dictionary<string, Fingerprint>> FingerprintsAsync(params string[] paths)
    {
        var result = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
            result.Add(path, new(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))),
                new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));
        return result;
    }

    private static async Task AssertOriginalsUnchangedAsync(Dictionary<string, Fingerprint> originals)
    {
        var after = await FingerprintsAsync(originals.Keys.ToArray());
        foreach (var pair in originals) Assert.Equal(pair.Value, after[pair.Key]);
    }

    private sealed class CountingProbe : IFileSystemObservationProbe
    {
        private readonly FileSystemObservationProbe _probe = new();
        public List<string> Files { get; } = [];
        public List<string> Roots { get; } = [];
        public FileSystemProbeResult ProbeFile(string path) { Files.Add(path); return _probe.ProbeFile(path); }
        public FileSystemProbeResult ProbeRoot(string path) { Roots.Add(path); return _probe.ProbeRoot(path); }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
