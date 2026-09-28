using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoShelf.Application.Background;
using PhotoShelf.Desktop;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

public sealed class AsyncMediaImageTests
{
    [Fact]
    public Task RecycledImageCannotPublishPreviousPathAndUnloadReleasesReservation() => OnStaAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "photoshelf-wpf-image-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "first.png");
        var second = Path.Combine(directory, "second.png");
        CreatePng(first, blue: 0, red: 255);
        CreatePng(second, blue: 255, red: 0);
        var workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockers = Enumerable.Range(0, workers).Select(_ => BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.VisiblePreview, async token =>
        {
            if (Interlocked.Increment(ref started) == workers) allStarted.SetResult();
            await release.Task.WaitAsync(token);
            return 0;
        })).ToArray();
        var image = new Image();
        AsyncMediaImage.SetDecodeWidth(image, 600); // Bypass disk thumbnail cache; these tests never use the user's cache.
        var window = new Window { Content = image, Width = 100, Height = 100, ShowInTaskbar = false };
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Show();
            AsyncMediaImage.SetPath(image, first);
            AsyncMediaImage.SetPath(image, second);
            release.SetResult();
            await Task.WhenAll(blockers);
            await WaitUntilAsync(() => image.Source is BitmapSource);
            var bitmap = (BitmapSource)image.Source;
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            Assert.Equal(255, pixels[0]);
            Assert.Equal(0, pixels[2]);
            Assert.True(AsyncMediaImage.ReservedDecodedBytes > 0);
            window.Content = null;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await WaitUntilAsync(() => AsyncMediaImage.ReservedDecodedBytes == 0);
            Assert.Null(image.Source);
        }
        finally
        {
            release.TrySetResult();
            window.Close();
            await Task.WhenAll(blockers);
            Directory.Delete(directory, true);
        }
    });

    [Fact]
    public Task NewObservationReloadsSamePathEvenWhenSizeAndTimestampAreUnchanged() => OnStaAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "photoshelf-wpf-image-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "changed.bmp");
        // Uncompressed BMP makes the equal-size rewrite deterministic.
        void WriteBitmap(byte blue, byte red)
        {
            var pixels = new byte[32 * 32 * 4];
            for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = blue; pixels[i + 2] = red; pixels[i + 3] = 255; }
            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 128)));
            using var stream = File.Create(path); encoder.Save(stream);
        }
        WriteBitmap(0, 255);
        var timestamp = File.GetLastWriteTimeUtc(path); var size = new FileInfo(path).Length;
        var image = new Image(); AsyncMediaImage.SetDecodeWidth(image, 600);
        var window = new Window { Content = image, Width = 100, Height = 100, ShowInTaskbar = false };
        try
        {
            window.Show(); AsyncMediaImage.SetRevision(image, 1); AsyncMediaImage.SetPath(image, path);
            await WaitUntilAsync(() => image.Source is BitmapSource);
            var old = image.Source;
            WriteBitmap(255, 0); File.SetLastWriteTimeUtc(path, timestamp);
            Assert.Equal(size, new FileInfo(path).Length);
            AsyncMediaImage.SetRevision(image, 2);
            await WaitUntilAsync(() => image.Source is BitmapSource && !ReferenceEquals(image.Source, old));
            var bitmap = new FormatConvertedBitmap((BitmapSource)image.Source, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            Assert.Equal(255, pixels[0]); Assert.Equal(0, pixels[2]);
        }
        finally
        {
            window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Directory.Delete(directory, true);
        }
    });

    [Fact]
    public Task HidingImageStopsDecodeAndReleasesItsPixels() => OnStaAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "photoshelf-wpf-image-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "image.png");
        CreatePng(path, blue: 128, red: 128);
        var image = new Image();
        AsyncMediaImage.SetDecodeWidth(image, 600);
        var window = new Window { Content = image, Width = 100, Height = 100, ShowInTaskbar = false };
        try
        {
            window.Show();
            AsyncMediaImage.SetPath(image, path);
            await WaitUntilAsync(() => image.Source is not null);
            image.Visibility = Visibility.Collapsed;
            await WaitUntilAsync(() => AsyncMediaImage.ReservedDecodedBytes == 0);
            Assert.Null(image.Source);
            image.Visibility = Visibility.Visible;
            await WaitUntilAsync(() => image.Source is not null);
        }
        finally
        {
            window.Close();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Directory.Delete(directory, true);
        }
    });

    [Fact]
    public Task NestedFileOperationPausesDeferNewDecodesUntilLastLeaseIsReleased() => OnStaAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "photoshelf-wpf-image-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "paused.png");
        CreatePng(path, blue: 32, red: 64);
        var image = new Image();
        AsyncMediaImage.SetDecodeWidth(image, 600);
        var window = new Window { Content = image, Width = 100, Height = 100, ShowInTaskbar = false };
        IDisposable? outer = null;
        IDisposable? inner = null;
        try
        {
            window.Show();
            outer = await AsyncMediaImage.PauseForFileOperationsAsync();
            inner = await AsyncMediaImage.PauseForFileOperationsAsync();
            AsyncMediaImage.SetPath(image, path);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Null(image.Source);
            Assert.Equal(0, AsyncMediaImage.ReservedDecodedBytes);
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            inner.Dispose(); inner = null;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Null(image.Source);
            outer.Dispose(); outer = null;
            await WaitUntilAsync(() => image.Source is not null);
        }
        finally
        {
            inner?.Dispose(); outer?.Dispose();
            window.Close();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Directory.Delete(directory, true);
        }
    });

    [Fact]
    public Task FileOperationPauseDrainsCancelledQueuedPreviewsWithoutWaitingForOtherJobs() => OnStaAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "photoshelf-wpf-image-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "queued.png");
        CreatePng(path, blue: 32, red: 64);
        var workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockers = Enumerable.Range(0, workers).Select(_ => BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.VisiblePreview, async token =>
        {
            if (Interlocked.Increment(ref started) == workers) allStarted.SetResult();
            await release.Task.WaitAsync(token);
            return 0;
        })).ToArray();
        var image = new Image();
        AsyncMediaImage.SetDecodeWidth(image, 600);
        var window = new Window { Content = image, Width = 100, Height = 100, ShowInTaskbar = false };
        IDisposable? pause = null;
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Show();
            AsyncMediaImage.SetPath(image, path);
            pause = await AsyncMediaImage.PauseForFileOperationsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(release.Task.IsCompleted);
            Assert.Null(image.Source);
            Assert.Equal(0, AsyncMediaImage.ReservedDecodedBytes);
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            release.SetResult();
            await Task.WhenAll(blockers);
            pause.Dispose(); pause = null;
            await WaitUntilAsync(() => image.Source is not null);
        }
        finally
        {
            release.TrySetResult();
            pause?.Dispose();
            window.Close();
            await Task.WhenAll(blockers);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Directory.Delete(directory, true);
        }
    });

    [Fact]
    public Task ScopedFileOperationPauseLeavesOtherImagesVisibleAndDecodable() => OnStaAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "photoshelf-wpf-image-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var movedPath = Path.Combine(directory, "moved.png");
        var unrelatedPath = Path.Combine(directory, "unrelated.png");
        CreatePng(movedPath, blue: 32, red: 64);
        CreatePng(unrelatedPath, blue: 64, red: 32);
        var moved = new Image(); var unrelated = new Image();
        AsyncMediaImage.SetDecodeWidth(moved, 600); AsyncMediaImage.SetDecodeWidth(unrelated, 600);
        var panel = new StackPanel(); panel.Children.Add(moved); panel.Children.Add(unrelated);
        var window = new Window { Content = panel, Width = 120, Height = 300, ShowInTaskbar = false };
        IDisposable? pause = null;
        try
        {
            window.Show();
            AsyncMediaImage.SetPath(moved, movedPath);
            AsyncMediaImage.SetPath(unrelated, unrelatedPath);
            await WaitUntilAsync(() => moved.Source is not null && unrelated.Source is not null);
            var unrelatedSource = unrelated.Source;
            pause = await AsyncMediaImage.PauseForFileOperationsAsync(new HashSet<string> { movedPath.ToUpperInvariant() });
            Assert.Null(moved.Source);
            Assert.Same(unrelatedSource, unrelated.Source);
            Assert.True(AsyncMediaImage.ReservedDecodedBytes > 0);
            using (File.Open(movedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            // A newly requested unrelated image still decodes while the file operation runs.
            AsyncMediaImage.SetPath(unrelated, null);
            AsyncMediaImage.SetPath(unrelated, unrelatedPath);
            await WaitUntilAsync(() => unrelated.Source is not null);
            Assert.Null(moved.Source);
            pause.Dispose(); pause = null;
            await WaitUntilAsync(() => moved.Source is not null);
        }
        finally
        {
            pause?.Dispose();
            window.Close();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Directory.Delete(directory, true);
        }
    });

    private static void CreatePng(string path, byte blue, byte red)
    {
        var bytes = new byte[32 * 32 * 4];
        for (var i = 0; i < bytes.Length; i += 4) { bytes[i] = blue; bytes[i + 2] = red; bytes[i + 3] = 255; }
        var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, bytes, 128);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private static Task OnStaAsync(Func<Task> body) => WpfTestDispatcher.RunAsync(body);
}
