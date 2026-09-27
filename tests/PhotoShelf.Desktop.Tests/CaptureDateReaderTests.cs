using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoShelf.Application.Metadata;
using PhotoShelf.Desktop;
using SkiaSharp;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

public sealed class CaptureDateReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-decoder-test-" + Guid.NewGuid().ToString("N"));
    public CaptureDateReaderTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("jpg")][InlineData("png")][InlineData("gif")][InlineData("webp")][InlineData("tiff")]
    public void SupportedSyntheticImagesDecodeAndAbsenceIsAnExplicitResult(string extension)
    {
        var path = Path.Combine(_root, "image." + extension);
        CreateImage(path, extension);
        var result = CaptureDateReader.Read(path); Assert.Equal(MetadataReadStatus.Absent, result.Status);
        if (extension == "webp") Assert.Single(MediaBitmapLoader.ReadAnimation(path, 16, 1));
        else
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(16, frame.PixelWidth); Assert.Equal(12, frame.PixelHeight);
        }
    }
    [Fact] public void ExistingExifDateIsReadAndLockedFileIsRetryable()
    {
        var path = Path.Combine(_root, "image.jpg"); CreateImage(path, "jpg"); AddExif(path, "2020:01:02 03:04:05");
        var result = CaptureDateReader.Read(path); Assert.Equal(MetadataReadStatus.Found, result.Status);
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5), result.CaptureDate);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(MetadataReadStatus.TransientError, CaptureDateReader.Read(path).Status);
    }
    [Fact] public void MissingFileIsNeverSuccessfulAbsence() =>
        Assert.Equal(MetadataReadStatus.TransientError, CaptureDateReader.Read(Path.Combine(_root, "missing.jpg")).Status);

    [Fact] public void CorruptRecognizedJpegIsNeverSuccessfulAbsence()
    {
        var path = Path.Combine(_root, "bad.jpg"); File.WriteAllBytes(path, [0xff,0xd8,0xff,0xe0,0x00,0x10,0x4a,0x46,0x49,0x46,0x00]);
        var result = CaptureDateReader.Read(path); Assert.NotEqual(MetadataReadStatus.Absent, result.Status); Assert.Null(result.CaptureDate);
    }
    [Fact] public void InvalidExifDateDoesNotErasePreviousDate()
    {
        var path = Path.Combine(_root, "invalid-date.jpg"); CreateImage(path, "jpg"); AddExif(path, "invalid capture date");
        var result = CaptureDateReader.Read(path); Assert.Equal(MetadataReadStatus.Corrupt, result.Status);
        var previous = new DateTime(2010, 1, 1); Assert.Equal(previous, result.ApplyTo(previous));
    }
    [Fact] public void XmpOnlyDateIsNotReportedAbsent()
    {
        var path = Path.Combine(_root, "xmp-only.jpg"); CreateImage(path, "jpg");
        var xml = "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:exif='http://ns.adobe.com/exif/1.0/' exif:DateTimeOriginal='2021-02-03T04:05:06'/></rdf:RDF></x:xmpmeta>";
        AddApp1(path, Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + xml));
        var result = CaptureDateReader.Read(path);
        Assert.Equal(MetadataReadStatus.Found, result.Status); Assert.Equal(new DateTime(2021, 2, 3, 4, 5, 6), result.CaptureDate);
    }

    private static void CreateImage(string path, string format)
    {
        if (format is "gif" or "tiff")
        {
            var pixels = Enumerable.Repeat((byte)255, 16 * 12 * 4).ToArray();
            var bitmap = BitmapSource.Create(16, 12, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
            BitmapEncoder encoder = format == "gif" ? new GifBitmapEncoder() : new TiffBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
        }
        else
        {
            using var bitmap = new SKBitmap(16, 12); bitmap.Erase(SKColors.Coral);
            using var encoded = bitmap.Encode(format == "jpg" ? SKEncodedImageFormat.Jpeg : format == "webp" ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Png, 100);
            using var output = File.Create(path); encoded.SaveTo(output);
        }
    }
    private static void AddExif(string path, string date)
    {
        var dateBytes = Encoding.ASCII.GetBytes(date + "\0");
        using var payload = new MemoryStream(); using var writer = new BinaryWriter(payload, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("Exif\0\0II")); writer.Write((ushort)42); writer.Write((uint)8);
        writer.Write((ushort)1); writer.Write((ushort)0x8769); writer.Write((ushort)4); writer.Write((uint)1); writer.Write((uint)26); writer.Write((uint)0);
        writer.Write((ushort)1); writer.Write((ushort)0x9003); writer.Write((ushort)2); writer.Write((uint)dateBytes.Length); writer.Write((uint)44); writer.Write((uint)0);
        writer.Write(dateBytes); writer.Flush(); AddApp1(path, payload.ToArray());
    }
    private static void AddApp1(string path, byte[] payload)
    {
        var jpeg = File.ReadAllBytes(path); var length = checked((ushort)(payload.Length + 2));
        using var output = File.Create(path); output.Write(jpeg, 0, 2);
        output.Write(new byte[] { 0xff, 0xe1, (byte)(length >> 8), (byte)length }); output.Write(payload); output.Write(jpeg, 2, jpeg.Length - 2);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
