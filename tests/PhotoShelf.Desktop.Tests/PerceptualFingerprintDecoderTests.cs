using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

public sealed class PerceptualFingerprintDecoderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-fingerprint-decoder-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BoundedDecoderProducesStableFingerprintForSamePixels()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "gradient.png");
        var pixels = new byte[48 * 32 * 4];
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 48; x++)
            {
                var offset = (y * 48 + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = (byte)(x * 255 / 47);
                pixels[offset + 3] = 255;
            }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(48, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 48 * 4)));
        using (var output = File.Create(path)) encoder.Save(output);

        var first = MediaBitmapLoader.LoadPerceptualFingerprint(path);
        var second = MediaBitmapLoader.LoadPerceptualFingerprint(path);

        Assert.Equal(first, second);
        Assert.Equal(64, Math.Max(first.PixelWidth, first.PixelHeight));
        Assert.InRange(first.PixelWidth / (double)first.PixelHeight, 1.45, 1.55);
    }

    // Synthetic animated WebP, matching the independently exercised preview fixture.
    private const string AnimatedWebp = "UklGRrYAAABXRUJQVlA4WAoAAAASAAAAAgAAAAAAQU5JTQYAAAAAAAAAAABBTk1GKAAAAAAAAAAAAAIAAAAAAEYAAANWUDhMDwAAAC8CAAAABxD9j/4HIqL/AQBBTk1GKgAAAAAAAAAAAAEAAAAAAIIAAAFWUDhMEQAAAC8BAAAQDxAx//MfjApE9D8AAEFOTUYoAAAAAQAAAAAAAAAAAAAA0gAAAFZQOEwPAAAALwAAAAAH0P+I/gciov8BAA==";

    [Theory]
    [InlineData(false, ".webp")]
    [InlineData(false, ".jpg")]
    [InlineData(true, ".webp")]
    [InlineData(true, ".jpg")]
    public void BundledWebpFingerprintIsStableBoundedAndReadOnly(bool animated, string extension)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "synthetic" + extension);
        if (animated) File.WriteAllBytes(path, Convert.FromBase64String(AnimatedWebp));
        else
        {
            using var bitmap = new SKBitmap(320, 240);
            using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Coral);
            using var paint = new SKPaint { Color = SKColors.Navy };
            canvas.DrawRect(100, 40, 80, 120, paint);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, 90);
            File.WriteAllBytes(path, encoded.ToArray());
        }
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        var length = new FileInfo(path).Length;
        var modified = File.GetLastWriteTimeUtc(path);

        var first = MediaBitmapLoader.LoadPerceptualFingerprint(path);
        var second = MediaBitmapLoader.LoadPerceptualFingerprint(path);

        Assert.Equal(first, second);
        Assert.InRange(first.PixelWidth, 1, 64); Assert.InRange(first.PixelHeight, 1, 64);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void WebpFingerprintCancellationAndMalformedOrOversizedInputDoNotChangeOriginal()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "synthetic.webp");
        var bytes = Convert.FromBase64String(AnimatedWebp);
        File.WriteAllBytes(path, bytes);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => MediaBitmapLoader.LoadPerceptualFingerprint(path, cancelled.Token));
        Assert.Equal(bytes, File.ReadAllBytes(path));

        File.WriteAllBytes(path, bytes[..40]);
        Assert.ThrowsAny<Exception>(() => MediaBitmapLoader.LoadPerceptualFingerprint(path));
        Assert.Equal(bytes[..40], File.ReadAllBytes(path));

        File.WriteAllBytes(path, bytes);
        using (var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            output.SetLength(128L * 1024 * 1024 + 1);
        var modified = File.GetLastWriteTimeUtc(path);
        Assert.Throws<NotSupportedException>(() => MediaBitmapLoader.LoadPerceptualFingerprint(path));
        Assert.Equal(128L * 1024 * 1024 + 1, new FileInfo(path).Length);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
