using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoShelf.Application.Background;

namespace PhotoShelf.Desktop;

public static class ThumbnailCache
{
    private static readonly OwnedThumbnailCache Store = new(Path.Combine(LocalCatalogStore.CatalogDirectory, "thumb-cache-v2"), 512L * 1024 * 1024);
    private static long _lastTrimTicks;
    private static int _trimScheduled;

    // All callers run in background workers. This cache exclusively owns generated ps-thumb-v2-* entries.
    public static ImageSource? LoadOrCreate(string path, int decodeWidth, Func<string, int, ImageSource?> factory)
    {
        ScheduleTrim();
        string? key = null;
        try
        {
            var source = new FileInfo(path);
            if (!source.Exists) return null;
            key = Store.GetKey(source.FullName, source.Length, source.LastWriteTimeUtc.Ticks, decodeWidth);
            if (Store.TryGetPath(key) is { } cachedPath && LoadBitmap(cachedPath, decodeWidth) is { } cached)
            {
                ScheduleTrim();
                return cached;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }

        // Decode once: a failed cache write must never trigger a second decode.
        var image = factory(path, decodeWidth);
        if (image is BitmapSource bitmap && key is not null)
        {
            try
            {
                Store.Write(key, stream =>
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(stream);
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        ScheduleTrim();
        return image;
    }

    private static BitmapSource? LoadBitmap(string path, int decodeWidth)
    {
        try
        {
            return ImageSharpBitmapLoader.LoadWicBounded(path, decodeWidth);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException) { return null; }
    }

    private static void ScheduleTrim()
    {
        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _lastTrimTicks) < TimeSpan.FromSeconds(30).Ticks || Interlocked.CompareExchange(ref _trimScheduled, 1, 0) != 0) return;
        _ = TrimAsync();
    }
    private static async Task TrimAsync()
    {
        try
        {
            await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Maintenance, token => Store.Trim(token, reserveBytes: 64L * 1024 * 1024)).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastTrimTicks, DateTime.UtcNow.Ticks);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        finally { Volatile.Write(ref _trimScheduled, 0); }
    }
}
