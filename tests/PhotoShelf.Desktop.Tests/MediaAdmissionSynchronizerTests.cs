using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Metadata;
using PhotoShelf.Desktop;
using PhotoShelf.Infrastructure.Sqlite;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

// Synthetic owned files only. The production scanner and synchronizer are exercised;
// the attribute probe supplies deterministic identity without requiring a particular volume.
public sealed class MediaAdmissionSynchronizerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "photoshelf-admission-sync-" + Guid.NewGuid().ToString("N"));
    private string Media => Path.Combine(_directory, "media");
    private static readonly DateTime Checked = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAndWatchAdmissionSkipNewSourceTextButKeepTransportAndUnknown(bool changedPaths)
    {
        var paths = new[]
        {
            Write("SelectText.d.ts", Encoding.UTF8.GetBytes("export type Selection = string;\n")),
            Write("source.mts", Encoding.UTF8.GetBytes("export const selected = true;\n")),
            Write("video.ts", TransportPackets(188, 0)),
            Write("camera.mts", TransportPackets(192, 4)),
            Write("uncertain.ts", [0xff, 0x00, 0x47]),
            Write("photo.jpg", [1, 2, 3])
        };
        var fingerprints = Fingerprints(paths);
        var store = await CreateAsync(); var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store,
            update => { updates.Add(update); return Task.CompletedTask; }, new Probe());
        await synchronizer.ProcessAsync(new(1, changedPaths ? paths : [], changedPaths ? [] : [Media], []),
            new FolderInclusionRules([]), Media, browseRecursively: false, includeSystem: true,
            CancellationToken.None, libraryRoots: [Media]);

        Assert.Null(await store.GetItemAsync(paths[0])); Assert.Null(await store.GetItemAsync(paths[1]));
        foreach (var path in paths.Skip(2)) Assert.NotNull(await store.GetItemAsync(path));
        Assert.Equal(4, await store.CountAsync(new() { IncludeSystemFolders = true }));
        Assert.True((await store.GetItemAsync(paths[2]))!.IsVideo);
        Assert.True((await store.GetItemAsync(paths[3]))!.IsVideo);
        Assert.True((await store.GetItemAsync(paths[4]))!.IsVideo);
        Assert.True(Assert.Single(updates, update => update.Completed).CatalogChanged);
        Assert.Equal(paths.Skip(2).OrderBy(path => path), updates.SelectMany(update => update.Items)
            .Select(item => item.Path).Distinct().OrderBy(path => path));
        Assert.Equal(fingerprints, Fingerprints(paths));
    }

    [Fact]
    public async Task TextOnlyWatchEventDoesNotCreateRowsOrClaimCatalogChanges()
    {
        var path = Write("Paste.d.ts", Encoding.UTF8.GetBytes("export interface Paste { text: string; }"));
        var store = await CreateAsync(); var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store,
            update => { updates.Add(update); return Task.CompletedTask; }, new Probe());
        await synchronizer.ProcessAsync(new(1, [path], [], []), new FolderInclusionRules([]), Media,
            false, true, CancellationToken.None, libraryRoots: [Media]);
        Assert.Null(await store.GetItemAsync(path));
        Assert.False(Assert.Single(updates, update => update.Completed).CatalogChanged);
        Assert.All(updates, update => Assert.Empty(update.Items));
    }

    [Fact]
    public async Task ExistingLegacySourceRecordIsNeitherRemovedNorReclassifiedAndRetainsAllKnownState()
    {
        var path = Write("legacy.d.ts", Encoding.UTF8.GetBytes("export const legacy = 1;"));
        var fingerprints = Fingerprints([path]); var store = await CreateAsync();
        var item = LibraryCatalogSynchronizer.Available(new Probe().ProbeFile(path));
        item.CaptureDate = new DateTime(2020, 1, 2); item.MetadataIndexed = true;
        item.MetadataStatus = MetadataReadStatus.Found; item.MetadataAttemptedAtUtc = Checked;
        item.IsFavorite = true;
        await store.UpsertItemsAsync([item]);
        var before = JsonSerializer.Serialize((await store.GetItemAsync(path))!);
        var updates = new List<LibraryCatalogUpdate>();
        var synchronizer = new LibraryCatalogSynchronizer(store,
            update => { updates.Add(update); return Task.CompletedTask; }, new Probe());
        Assert.False(await synchronizer.ObservePathsAsync([path], forceContent: false, CancellationToken.None));
        Assert.Empty(updates);
        await synchronizer.ProcessAsync(new(1, [], [Media], []), new FolderInclusionRules([]), Media,
            false, true, CancellationToken.None, libraryRoots: [Media]);

        Assert.Equal(before, JsonSerializer.Serialize((await store.GetItemAsync(path))!));
        Assert.Equal(1, await store.CountAsync(new() { IncludeSystemFolders = true }));
        Assert.False(Assert.Single(updates, update => update.Completed).CatalogChanged);
        Assert.All(updates, update => { Assert.Empty(update.Items); Assert.Empty(update.Renames); });
        Assert.Equal(fingerprints, Fingerprints([path]));
    }

    private async Task<SqliteDesktopCatalogStore> CreateAsync()
    {
        var store = new SqliteDesktopCatalogStore(Path.Combine(_directory, "catalog"));
        await store.InitializeAsync(); return store;
    }

    private string Write(string name, byte[] bytes)
    {
        Directory.CreateDirectory(Media); var path = Path.Combine(Media, name);
        File.WriteAllBytes(path, bytes); return path;
    }

    private static byte[] TransportPackets(int stride, int offset)
    {
        var bytes = Enumerable.Repeat((byte)0xff, offset + stride * 6).ToArray();
        for (var packet = 0; packet < 6; packet++)
        {
            var start = offset + packet * stride;
            bytes[start] = 0x47; bytes[start + 1] = 0x1f; bytes[start + 2] = 0xff; bytes[start + 3] = 0x10;
        }
        return bytes;
    }

    private static string[] Fingerprints(IEnumerable<string> paths) => paths.Select(path =>
        path + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) + "|" + File.GetLastWriteTimeUtc(path).Ticks).ToArray();

    private sealed class Probe : IFileSystemObservationProbe
    {
        public FileSystemProbeResult ProbeFile(string path)
        {
            var file = new FileInfo(path);
            return new(path, FileAvailability.Available, Checked, file.Length, file.LastWriteTimeUtc,
                FileIdentity: "synthetic:" + path);
        }
        public FileSystemProbeResult ProbeRoot(string path) =>
            new(path, FileAvailability.Available, Checked, IsDirectory: true);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
