using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
