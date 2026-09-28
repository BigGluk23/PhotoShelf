using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Desktop;
using PhotoShelf.Infrastructure.Sqlite;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

// Actual WPF windows and SQLite, with owned synthetic images only. Offline is a catalog
// observation injection, not evidence of physically disconnecting a Windows storage device.
public sealed class LibraryAvailabilityViewerTests
{
    private static readonly DateTime Modified = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Theory]
    [InlineData(FileAvailability.Missing, "Файл отсутствует")]
    [InlineData(FileAvailability.RootOffline, "Диск отключён")]
    public Task UnavailableCurrentPhotoShowsNoticeAndReappearanceDecodesNewPixels(
        FileAvailability unavailable, string expectedNotice) => OnStaAsync(async () =>
    {
        var root = NewRoot(); var source = Path.Combine(root, "current.png");
        var retained = Path.Combine(root, "original-retained.png");
        PhotoViewerWindow? viewer = null;
        try
        {
            CreatePng(source, blue: 0, red: 255);
            var originalBytes = File.ReadAllBytes(source);
            var store = new SqliteDesktopCatalogStore(Path.Combine(root, "catalog"));
            await store.InitializeAsync();
            await store.UpsertItemsAsync([Available(source, "synthetic-current", favorite: true)]);
            var original = (await store.GetItemAsync(source))!;
            viewer = CreateViewer(store, count: 1, anchor: source);
            var image = (PreviewImage)viewer.FindName("PhotoImage");
            var status = (TextBlock)viewer.FindName("PhotoStatusText");
            viewer.Show();
            await UntilAsync(() => image.Source is BitmapSource);
            AssertPixel(image, blue: 0, red: 255);

            // Preserve the first fixture and make its original path genuinely unavailable.
            File.Move(source, retained);
            Assert.True(await store.ApplyObservationAsync(new(new SavedMediaItem
            {
                Path = source, Availability = unavailable, AvailabilityCheckedAtUtc = DateTime.UtcNow,
                AvailabilityErrorCode = "SyntheticUnavailable"
            }), original));
            var missing = (await store.GetItemAsync(source))!;
            viewer.CatalogItemsChanged([missing], []);
            await UntilAsync(() => image.Source is null && status.Text == expectedNotice && status.Visibility == Visibility.Visible);
            Assert.Null(AsyncMediaImage.GetPath(image));
            Assert.Equal(original.AssetId, missing.AssetId);
            Assert.True(missing.IsFavorite);
            Assert.Equal(original.CaptureDate, missing.CaptureDate);

            // A replacement with the same file date must not reuse the previous red pixels.
            CreatePng(source, blue: 255, red: 0);
            Assert.True(await store.ApplyObservationAsync(new(Available(source, "synthetic-current"),
                ForceContentRevalidation: true), missing));
            var restored = (await store.GetItemAsync(source))!;
            viewer.CatalogItemsChanged([restored], []);
            await UntilAsync(() => image.Source is BitmapSource && string.IsNullOrEmpty(status.Text)
                && status.Visibility == Visibility.Collapsed);
            AssertPixel(image, blue: 255, red: 0);
            Assert.Equal(source, AsyncMediaImage.GetPath(image));
            Assert.Equal(original.AssetId, restored.AssetId);
            Assert.True(restored.IsFavorite);
            Assert.Equal(originalBytes, File.ReadAllBytes(retained));
            Assert.Equal(1, await store.CountAsync(new() { IncludeSystemFolders = true }));
        }
        finally { await CloseAndDeleteFixtureAsync(viewer, root); }
    });

    [Fact]
    public Task ExternalRenameKeepsCurrentPhotoAndFilmstripSelectionWhenItsSortPositionChanges() => OnStaAsync(async () =>
    {
        var root = NewRoot(); var source = Path.Combine(root, "a-current.png");
        var other = Path.Combine(root, "m-other.png"); var destination = Path.Combine(root, "z-current.png");
        PhotoViewerWindow? viewer = null;
        try
        {
            CreatePng(source, blue: 0, red: 255); CreatePng(other, blue: 255, red: 0);
            var originalHash = SHA256.HashData(File.ReadAllBytes(source));
            var store = new SqliteDesktopCatalogStore(Path.Combine(root, "catalog")); await store.InitializeAsync();
            await store.UpsertItemsAsync([Available(source, "synthetic-current", favorite: true), Available(other, "synthetic-other")]);
            var original = (await store.GetItemAsync(source))!;
            var before = await store.QueryPageAsync(Query, 0, 2);
            Assert.Equal(source, before.Items[0].Path);
            viewer = CreateViewer(store, count: 2, anchor: source);
            var image = (PreviewImage)viewer.FindName("PhotoImage");
            var title = (TextBlock)viewer.FindName("TitleText");
            viewer.Show(); await UntilAsync(() => image.Source is BitmapSource);
            AssertPixel(image, blue: 0, red: 255);
            Assert.Equal(original.AssetId, Assert.Single(viewer.FilmstripItems, cell => cell.IsCenter).Item.AssetId);

            File.Move(source, destination);
            Assert.True(await store.TryReconcileExternalRenameAsync(original, Available(destination, "synthetic-current")));
            var renamed = (await store.GetItemAsync(destination))!;
            // Prove the former numeric index now refers to a different picture.
            var after = await store.QueryPageAsync(Query, 0, 2);
            Assert.Equal(other, after.Items[0].Path);
            Assert.Equal(destination, after.Items[1].Path);
            viewer.CatalogItemsChanged([renamed], [new CatalogExternalRename(source, renamed)]);
            await UntilAsync(() => AsyncMediaImage.GetPath(image) == destination && image.Source is BitmapSource
                && title.Text == Path.GetFileName(destination));
            AssertPixel(image, blue: 0, red: 255);
            var center = Assert.Single(viewer.FilmstripItems, cell => cell.IsCenter);
            Assert.Equal(original.AssetId, center.Item.AssetId);
            Assert.Equal(destination, center.Item.Path);
            Assert.True(center.Item.IsFavorite);
            Assert.Equal(1, center.AbsoluteIndex);
            Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(destination)));
            Assert.Null(await store.GetItemAsync(source));
            Assert.Equal(original.AssetId, renamed.AssetId);
        }
        finally { await CloseAndDeleteFixtureAsync(viewer, root); }
    });

    private static CatalogViewQuery Query => new() { IncludeSystemFolders = true, NewestFirst = false };
    private static PhotoViewerWindow CreateViewer(SqliteDesktopCatalogStore store, int count, string anchor)
    {
        var viewer = new PhotoViewerWindow(store, Query, count, 0, anchor) { ShowInTaskbar = false };
        // Preserve the real filmstrip data/selection but prevent its default thumbnail decoder
        // from touching the user's disk cache. The main image uses decodeWidth=2200, no disk cache.
        ((ListBox)viewer.FindName("FilmstripList")).Visibility = Visibility.Collapsed;
        return viewer;
    }
    private static SavedMediaItem Available(string path, string identity, bool favorite = false) => new()
    {
        Path = path, SizeBytes = new FileInfo(path).Length, FileModifiedAt = File.GetLastWriteTimeUtc(path),
        FileIdentity = identity, Availability = FileAvailability.Available, AvailabilityCheckedAtUtc = DateTime.UtcNow,
        IsFavorite = favorite, CaptureDate = new DateTime(2020, 2, 3), MetadataIndexed = true,
        MetadataStatus = PhotoShelf.Application.Metadata.MetadataReadStatus.Found
    };
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoshelf-viewer-availability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static void CreatePng(string path, byte blue, byte red)
    {
        var pixels = new byte[32 * 32 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = blue; pixels[i + 2] = red; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 128);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(stream);
        File.SetLastWriteTimeUtc(path, Modified);
    }
    private static void AssertPixel(PreviewImage image, byte blue, byte red)
    {
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.Source);
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        Assert.Equal(blue, pixels[0]); Assert.Equal(red, pixels[2]); Assert.Equal(255, pixels[3]);
    }
    private static async Task CloseAndDeleteFixtureAsync(PhotoViewerWindow? viewer, string root)
    {
        viewer?.Close();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        // Even a failing assertion must drain actual readers before deleting our fixture.
        using var pause = await AsyncMediaImage.PauseForFileOperationsAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(15, timeout.Token);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static Task OnStaAsync(Func<Task> body) => WpfTestDispatcher.RunAsync(body);
}
