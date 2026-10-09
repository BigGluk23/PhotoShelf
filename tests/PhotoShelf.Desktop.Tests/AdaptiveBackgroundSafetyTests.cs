using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Background;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Infrastructure.Sqlite;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

public sealed class AdaptiveBackgroundSafetyTests
{
    [Theory]
    [InlineData(BackgroundLoadMode.Quiet)]
    [InlineData(BackgroundLoadMode.Balanced)]
    [InlineData(BackgroundLoadMode.Fast)]
    public async Task OfflineAndReconnectPreserveKnownMetadataIdentityAndOriginalBytes(BackgroundLoadMode mode)
    {
        var root = Directory.CreateTempSubdirectory("photoshelf-background-safety-").FullName;
        var media = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
        var path = Path.Combine(media, "fixture.png");
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(path, bytes);
        var modified = File.GetLastWriteTimeUtc(path);
        var hash = SHA256.HashData(bytes);
        var store = new SqliteDesktopCatalogStore(Path.Combine(root, "catalog"));
        try
        {
            await store.InitializeAsync();
            var saved = LibraryCatalogSynchronizer.Available(new FileSystemObservationProbe().ProbeFile(path));
            saved.IsFavorite = true; saved.CaptureDate = new DateTime(2020, 1, 2); saved.MetadataIndexed = true;
            await store.UpsertItemsAsync([saved]);
            var before = (await store.GetItemAsync(path))!;
            var probe = new AvailabilityProbe();
            var control = new BackgroundWorkController { Mode = mode };
            var synchronizer = new LibraryCatalogSynchronizer(store, _ => Task.CompletedTask, probe, control);
            for (var cycle = 0; cycle < 6; cycle++)
            {
                probe.Offline = cycle % 2 == 0;
                control.SetPowerState(cycle % 3 == 0); control.NoteInteraction();
                await synchronizer.ProcessAsync(new(cycle + 1, [], [media], []), new([]), null, true, true,
                    CancellationToken.None, [media]);
                var current = (await store.GetItemAsync(path))!;
                Assert.Equal(before.AssetId, current.AssetId);
                Assert.Equal(before.CaptureDate, current.CaptureDate);
                Assert.True(current.IsFavorite);
                Assert.Equal(probe.Offline ? FileAvailability.RootOffline : FileAvailability.Available, current.Availability);
                Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
                Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class AvailabilityProbe : IFileSystemObservationProbe
    {
        public bool Offline;
        private readonly FileSystemObservationProbe _real = new();
        public FileSystemProbeResult ProbeRoot(string path) => Offline
            ? new(path, FileAvailability.RootOffline, DateTime.UtcNow, ErrorCode: "SyntheticDisconnected", IsDirectory: true)
            : _real.ProbeRoot(path);
        public FileSystemProbeResult ProbeFile(string path) => Offline
            ? new(path, FileAvailability.RootOffline, DateTime.UtcNow, ErrorCode: "SyntheticDisconnected")
            : _real.ProbeFile(path);
    }
}
