using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoShelf.Desktop;
using PhotoShelf.Application.Metadata;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

public sealed class MediaDecoderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("photoshelf-media-fixture-").FullName;
    // Synthetic 3x1 fixtures generated with Pillow. No personal images or copied vendor fixtures.
    private const string Gif = "R0lGODlhAwABAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQEBwAAACwAAAAAAwABAAAIBQABCAwIACH5BA0NAAAALAAAAAADAAEAgQAAAAAA/wAAAAAAAAgGAAEEABAQACH5BAUVAAAALAEAAAACAAEAgQAAAAD/AAAAAAAAAAgFAAEECAgAOw==";
    private const string Webp = "UklGRrYAAABXRUJQVlA4WAoAAAASAAAAAgAAAAAAQU5JTQYAAAAAAAAAAABBTk1GKAAAAAAAAAAAAAIAAAAAAEYAAANWUDhMDwAAAC8CAAAABxD9j/4HIqL/AQBBTk1GKgAAAAAAAAAAAAEAAAAAAIIAAAFWUDhMEQAAAC8BAAAQDxAx//MfjApE9D8AAEFOTUYoAAAAAQAAAAAAAAAAAAAA0gAAAFZQOEwPAAAALwAAAAAH0P+I/gciov8BAA==";

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void AnimationIsDetectedByContentAndKeepsThreeFrameDurations(bool gif)
    {
        var path = Fixture(gif ? Gif : Webp, "disguised.jpg");
        var info = MediaBitmapLoader.ReadInfo(path)!;
        Assert.True(info.IsAnimationFormat); Assert.Equal(3, info.FrameCount);
        var frames = MediaBitmapLoader.ReadAnimation(path, 10, 128);
        Assert.Equal(new[] { 70, 130, 210 }, frames.Select(frame => frame.DelayMilliseconds));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(frames[0].Bitmap, 0));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(frames[1].Bitmap, 1));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(frames[2].Bitmap, 2));
        // GIF frame1 is restore-previous; frame2 must restore red, not retain frame1's blue pixel.
        Assert.Equal(gif ? new byte[] { 0, 0, 255, 255 } : new byte[] { 0, 0, 0, 0 }, Pixel(frames[2].Bitmap, 1));
        Assert.All(frames, frame => Assert.True(frame.Bitmap.IsFrozen));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [Fact]
    public void FirstFrameFallbackAndDimensionLimitAreExplicitAndCancelable()
    {
        var path = Fixture(Gif, "animation.gif");
        var only = Assert.Single(MediaBitmapLoader.ReadAnimation(path, 2, 1));
        Assert.InRange(only.Bitmap.PixelWidth, 1, 2); Assert.InRange(only.Bitmap.PixelHeight, 1, 2);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MediaBitmapLoader.ReadAnimation(path, 2, 3, cancellation.Token));
    }
    [Fact]
    public void TruncatedPayloadCannotReturnAnApparentlyValidAnimation()
    {
        var data = Convert.FromBase64String(Webp); var path = Path.Combine(_root, "truncated.webp");
        File.WriteAllBytes(path, data[..40]);
        Assert.ThrowsAny<Exception>(() => MediaBitmapLoader.ReadAnimation(path, 16, 3));
    }
    [Theory]
    [InlineData("gif")][InlineData("webp")]
    public void MalformedBigTiffDisguisedAsAnimationNeverReportsSuccessfulMetadata(string extension)
    {
        // Little-endian BigTIFF: 8-byte offsets, IFD at byte 16, impossibly large entry count.
        var path = Path.Combine(_root, "malformed." + extension);
        File.WriteAllBytes(path, [0x49, 0x49, 43, 0, 8, 0, 0, 0, 16, 0, 0, 0, 0, 0, 0, 0,
            0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x7f]);
        Assert.Null(MediaBitmapLoader.ReadInfo(path));
        Assert.Throws<NotSupportedException>(() => MediaBitmapLoader.ReadAnimation(path, 16, 3));
        var metadata = CaptureDateReader.Read(path);
        Assert.NotEqual(MetadataReadStatus.Found, metadata.Status);
        Assert.NotEqual(MetadataReadStatus.Absent, metadata.Status);
        Assert.Null(metadata.CaptureDate);
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public void OversizedAnimationIsRejectedBeforeNativeCodecCreation(bool gif)
    {
        var path = Fixture(gif ? Gif : Webp, "large.jpg");
        using (var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            output.SetLength(128L * 1024 * 1024 + 1);
        Assert.Throws<NotSupportedException>(() => MediaBitmapLoader.ReadInfo(path));
        Assert.Throws<NotSupportedException>(() => MediaBitmapLoader.ReadAnimation(path, 16, 1));
    }
    private string Fixture(string content, string name) { var path = Path.Combine(_root, name); File.WriteAllBytes(path, Convert.FromBase64String(content)); return path; }
    private static byte[] Pixel(BitmapSource bitmap, int x)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
        return bytes.Skip(x * 4).Take(4).ToArray();
    }
    public void Dispose() => Directory.Delete(_root, true);
}
