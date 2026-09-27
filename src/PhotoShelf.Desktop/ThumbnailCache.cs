using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoShelf.Desktop;

public static class ThumbnailCache
{
    private const string CacheVersion = "thumb-v1";

    public static ImageSource? LoadOrCreate(string path, int decodeWidth, Func<string, int, ImageSource?> factory)
    {
        try
        {
            var sourceFile = new FileInfo(path);
            if (!sourceFile.Exists)
            {
                return null;
            }

            Directory.CreateDirectory(CacheDirectory);
            var cachePath = GetCachePath(sourceFile, decodeWidth);
            if (File.Exists(cachePath))
            {
                var cached = LoadBitmap(cachePath, decodeWidth);
                if (cached is not null)
                {
                    return cached;
                }
            }

            var image = factory(path, decodeWidth);
            if (image is BitmapSource bitmapSource)
            {
                SavePng(cachePath, bitmapSource);
            }

            return image;
        }
        catch
        {
            return factory(path, decodeWidth);
        }
    }

    private static string CacheDirectory =>
        Path.Combine(LocalCatalogStore.CatalogDirectory, "thumb-cache");

    private static string GetCachePath(FileInfo file, int decodeWidth)
    {
        var stamp = $"{CacheVersion}|{decodeWidth}|{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(stamp));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(CacheDirectory, $"{hash}.png");
    }

    private static BitmapImage? LoadBitmap(string path, int decodeWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static void SavePng(string path, BitmapSource bitmap)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch
        {
            // Кэш ускоряет работу, но не должен мешать просмотру.
        }
    }
}
