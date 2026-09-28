using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoShelf.Application.Media;
using PhotoShelf.Application.Metadata;
using PhotoShelf.Desktop;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

public sealed class HeifDecoderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("photoshelf-heif-test-").FullName;

    [Theory]
    [InlineData("quadrants.heic", 320, 180)]
    [InlineData("quadrants-48mp.heic", 320, 240)]
    [InlineData("quadrants-no-date.heic", 320, 180)]
    public void BundledDecoderReadsHeicAndReleasesUnchangedOriginal(string name, int width, int height)
    {
        var path = Fixture(name);
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var bitmap = MediaBitmapLoader.LoadStillBounded(path, 320);
        Assert.Equal(width, bitmap.PixelWidth);
        Assert.Equal(height, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen);
        // HEVC is lossy: validate colour/placement, not exact compressed pixel values.
        AssertColour(bitmap, .4, .35, 240, 20, 20);
        AssertColour(bitmap, .75, .25, 20, 230, 20);
        AssertColour(bitmap, .25, .75, 20, 20, 240);
        AssertColour(bitmap, .75, .75, 230, 230, 20);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(before, SHA256.HashData(exclusive));
    }

    [Fact]
    public void ContainerRotationIsAppliedExactlyOnce()
    {
        var bitmap = MediaBitmapLoader.LoadStillBounded(Fixture("quadrants-rotated.heic"), 320);
        Assert.Equal(180, bitmap.PixelWidth);
        Assert.Equal(320, bitmap.PixelHeight);
        AssertColour(bitmap, .25, .25, 20, 20, 240);
        AssertColour(bitmap, .75, .25, 240, 20, 20);
        AssertColour(bitmap, .25, .75, 230, 230, 20);
        AssertColour(bitmap, .75, .75, 20, 230, 20);
    }

    [Fact]
    public void ContentDetectionWorksWithWrongExtensionAndViewerDoesNotUpscale()
    {
        var path = Fixture("quadrants.heic", "disguised.jpg");
        Assert.True(MediaBitmapLoader.IsHeif(path));
        Assert.Null(MediaBitmapLoader.ReadInfo(path));
        var bitmap = MediaBitmapLoader.LoadStillBounded(path, 2048);
        Assert.Equal(320, bitmap.PixelWidth);
        Assert.Equal(180, bitmap.PixelHeight);
    }

    [Fact]
    public void ExifCaptureDateAndAbsenceAreReadWithoutWindowsCodec()
    {
        var found = CaptureDateReader.Read(Fixture("quadrants.heic"));
        Assert.Equal(MetadataReadStatus.Found, found.Status);
        Assert.Equal(new DateTime(2026, 9, 28, 12, 34, 56), found.CaptureDate);
        var absent = CaptureDateReader.Read(Fixture("quadrants-no-date.heic"));
        Assert.Equal(MetadataReadStatus.Absent, absent.Status);
        Assert.Null(absent.CaptureDate);
    }

    [Fact]
    public async Task CorruptNativeDecodePreservesBytesAndDoesNotBlockNextImage()
    {
        var path = Fixture("quadrants.heic", "truncated.heic");
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..80]);
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var client = new HeifDecoderClient();
        await Assert.ThrowsAnyAsync<Exception>(() => client.DecodeAsync(path, 256));
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(before, SHA256.HashData(exclusive));
        var frame = await client.DecodeAsync(Fixture("quadrants.heic", "next.heic"), 256);
        Assert.Equal(256, frame.Width);
        Assert.Equal(144, frame.Height);
    }

    private string Fixture(string name, string? targetName = null)
    {
        var path = Path.Combine(_root, targetName ?? name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Heif", name), path);
        return path;
    }

    private static void AssertColour(BitmapSource bitmap, double x, double y, int red, int green, int blue)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
        var offset = ((int)(y * bitmap.PixelHeight) * bitmap.PixelWidth + (int)(x * bitmap.PixelWidth)) * 4;
        Assert.InRange((int)bytes[offset], Math.Max(0, blue - 35), Math.Min(255, blue + 35));
        Assert.InRange((int)bytes[offset + 1], Math.Max(0, green - 35), Math.Min(255, green + 35));
        Assert.InRange((int)bytes[offset + 2], Math.Max(0, red - 35), Math.Min(255, red + 35));
        Assert.Equal(255, bytes[offset + 3]);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
