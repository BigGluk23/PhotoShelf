using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Desktop;
using PhotoShelf.Infrastructure.Sqlite;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

public sealed class DirectoryReconciliationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "photoshelf-directory-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
    private string Root => Path.Combine(_directory, "library");

    [Fact]
    public async Task DirectoryDiscoveryFindsItsMediaWithoutProbingAnUnrelatedCatalogBranch()
    {
        var store = new SqliteDesktopCatalogStore(Path.Combine(_directory, "catalog"));
        await store.InitializeAsync();
        var untouched = await CreatePngAsync(Path.Combine(Root, "other", "existing.png"));
        var created = await CreatePngAsync(Path.Combine(Root, "new-folder", "nested", "new.png"));
        var probe = new CountingProbe();
        var existing = LibraryCatalogSynchronizer.Available(probe.ProbeFile(untouched));
        existing.IsFavorite = true; existing.CaptureDate = new DateTime(2020, 1, 2); existing.MetadataIndexed = true;
        await store.UpsertItemsAsync([existing]);
        var before = (await store.GetItemAsync(untouched))!;
        probe.Files.Clear(); probe.Roots.Clear();
        var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store, update => { updates.Add(update); return Task.CompletedTask; }, probe);
        var target = Path.Combine(Root, "new-folder");
        var beforeHash = SHA256.HashData(await File.ReadAllBytesAsync(created));
        var beforeModified = File.GetLastWriteTimeUtc(created);

        await synchronizer.ProcessAsync(new(1, [], [], [], DirectoryChanges: [new(Root, target)]),
            new FolderInclusionRules([]), null, false, true, CancellationToken.None, libraryRoots: [Root]);

        Assert.NotNull(await store.GetItemAsync(created));
        Assert.Equal(2, await store.CountAsync(new() { IncludeSystemFolders = true }));
        Assert.Equal([target], probe.Roots);
        Assert.DoesNotContain(untouched, probe.Files);
        Assert.All(probe.Files, path => Assert.Equal(created, path));
        var after = (await store.GetItemAsync(untouched))!;
        Assert.Equal(before.ObservationVersion, after.ObservationVersion);
        Assert.True(after.IsFavorite); Assert.True(after.MetadataIndexed); Assert.Equal(before.CaptureDate, after.CaptureDate);
        Assert.Empty(updates.SelectMany(update => update.Roots));
        Assert.Equal(beforeHash, SHA256.HashData(await File.ReadAllBytesAsync(created)));
        Assert.Equal(beforeModified, File.GetLastWriteTimeUtc(created));
        Assert.Equal(Png, await File.ReadAllBytesAsync(untouched));
    }

    [Fact]
    public async Task EmptyNonmediaDirectoryNoiseDoesNotProbeExistingMediaOrInvalidateMetadata()
    {
        var store = new SqliteDesktopCatalogStore(Path.Combine(_directory, "catalog"));
        await store.InitializeAsync();
        var untouched = await CreatePngAsync(Path.Combine(Root, "photos", "original.png"));
        var probe = new CountingProbe();
        var saved = LibraryCatalogSynchronizer.Available(probe.ProbeFile(untouched)); saved.MetadataIndexed = true;
        await store.UpsertItemsAsync([saved]);
        var before = (await store.GetItemAsync(untouched))!;
        var targets = Enumerable.Range(0, 32).Select(index => new LibraryDirectoryChange(Root,
            Path.Combine(Root, "application-data", "noise-" + index))).ToArray();
        foreach (var target in targets)
        {
            Directory.CreateDirectory(target.Path);
            await File.WriteAllTextAsync(Path.Combine(target.Path, "activity.log"), "synthetic noise");
        }
        probe.Files.Clear(); probe.Roots.Clear();
        var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store, update => { updates.Add(update); return Task.CompletedTask; }, probe);
        await synchronizer.ProcessAsync(new(1, [], [], [], DirectoryChanges: targets),
            new FolderInclusionRules([]), null, false, true, CancellationToken.None, libraryRoots: [Root]);

        Assert.Empty(probe.Files); Assert.Equal(targets.Select(target => target.Path), probe.Roots);
        Assert.All(updates, update => Assert.False(update.CatalogChanged));
        Assert.Equal(before.ObservationVersion, (await store.GetItemAsync(untouched))!.ObservationVersion);
        Assert.True((await store.GetItemAsync(untouched))!.MetadataIndexed);
        Assert.Equal(Png, await File.ReadAllBytesAsync(untouched));
    }

    [Fact]
    public async Task DirectoryRenameAndDeletionKeepAssetIdentityFavoritesAndCatalogRows()
    {
        var store = new SqliteDesktopCatalogStore(Path.Combine(_directory, "catalog"));
        await store.InitializeAsync();
        var oldFolder = Path.Combine(Root, "before"); var newFolder = Path.Combine(Root, "after");
        var source = await CreatePngAsync(Path.Combine(oldFolder, "original.png"));
        var destination = Path.Combine(newFolder, "original.png");
        var probe = new CountingProbe("synthetic-directory-rename-identity");
        var saved = LibraryCatalogSynchronizer.Available(probe.ProbeFile(source));
        saved.IsFavorite = true; saved.CaptureDate = new DateTime(2020, 1, 2); saved.MetadataIndexed = true;
        await store.UpsertItemsAsync([saved]);
        var before = (await store.GetItemAsync(source))!;
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var modified = File.GetLastWriteTimeUtc(source);
        // Simulate an external user rename on this owned synthetic fixture.
        Directory.Move(oldFolder, newFolder);
        var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store, update => { updates.Add(update); return Task.CompletedTask; }, probe);
        await synchronizer.ProcessAsync(new(1, [], [], [], DirectoryChanges: [new(Root, newFolder), new(Root, oldFolder)]),
            new FolderInclusionRules([]), null, false, true, CancellationToken.None, libraryRoots: [Root]);

        var renamed = (await store.GetItemAsync(destination))!;
        Assert.Null(await store.GetItemAsync(source)); Assert.Equal(before.AssetId, renamed.AssetId);
        Assert.True(renamed.IsFavorite); Assert.Equal(before.CaptureDate, renamed.CaptureDate);
        Assert.Equal(1, await store.CountAsync(new() { IncludeSystemFolders = true }));
        Assert.Single(updates.SelectMany(update => update.Renames)); Assert.Empty(updates.SelectMany(update => update.Roots));
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(destination)));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(destination));

        // Simulate external deletion; reconciliation must retain the now-missing catalog record.
        Directory.Delete(newFolder, recursive: true);
        updates.Clear();
        await synchronizer.ProcessAsync(new(2, [], [], [], DirectoryChanges: [new(Root, newFolder)]),
            new FolderInclusionRules([]), null, false, true, CancellationToken.None, libraryRoots: [Root]);
        var missing = (await store.GetItemAsync(destination))!;
        Assert.Equal(before.AssetId, missing.AssetId); Assert.Equal(FileAvailability.Missing, missing.Availability);
        Assert.True(missing.IsFavorite); Assert.Equal(before.CaptureDate, missing.CaptureDate);
        Assert.Equal(1, await store.CountAsync(new() { IncludeSystemFolders = true }));
        Assert.Empty(updates.SelectMany(update => update.Roots));
    }

    private static async Task<string> CreatePngAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Png);
        return path;
    }

    private sealed class CountingProbe(string? identity = null) : IFileSystemObservationProbe
    {
        private readonly FileSystemObservationProbe _probe = new();
        public List<string> Files { get; } = [];
        public List<string> Roots { get; } = [];
        public FileSystemProbeResult ProbeFile(string path)
        {
            Files.Add(path);
            var result = _probe.ProbeFile(path);
            return identity is not null && result.Availability == FileAvailability.Available ? result with { FileIdentity = identity } : result;
        }
        public FileSystemProbeResult ProbeRoot(string path) { Roots.Add(path); return _probe.ProbeRoot(path); }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
