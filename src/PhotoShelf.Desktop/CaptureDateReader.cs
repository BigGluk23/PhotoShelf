using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Xmp;
using PhotoShelf.Application.Metadata;
using PhotoShelf.Application.Media;

namespace PhotoShelf.Desktop;

public static class CaptureDateReader
{
    public static CaptureDateReadResult Read(string path)
    {
        try
        {
            // Windows denies write/delete sharing for the entire attempt, including the fallback.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var metadataRead = false;
            var malformed = false;
            try
            {
                var directories = ImageMetadataReader.ReadMetadata(stream, path);
                metadataRead = directories.Count > 1;
                malformed = directories.Any(directory => directory.Errors.Any());
                foreach (var directory in directories.OfType<ExifSubIfdDirectory>())
                    if (directory.GetString(ExifDirectoryBase.TagDateTimeOriginal) is { Length: > 0 } original)
                        return Parse(original);
                foreach (var xmp in directories.OfType<XmpDirectory>())
                {
                    var properties = xmp.GetXmpProperties();
                    foreach (var key in new[] { "exif:DateTimeOriginal", "photoshop:DateCreated", "xmp:CreateDate" })
                        if (properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return Parse(value);
                }
            }
            catch (ImageProcessingException) { malformed = true; }
            catch (NotSupportedException) { }
            // HEIF metadata is read by the bundled parser, independent of Windows extensions.
            // Do not turn an absent EXIF date into a recurring WIC codec error on clean Windows.
            stream.Position = 0;
            if (HeifDecoderClient.HasHeifSignature(stream))
                return malformed ? new(MetadataReadStatus.Corrupt, ErrorCode: "invalid-metadata") :
                    metadataRead ? new(MetadataReadStatus.Absent) : new(MetadataReadStatus.Unsupported, ErrorCode: "unsupported-metadata");
            // Legacy formats can also expose DateTaken through WIC.
            stream.Position = 0;
            try
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                if (decoder.Frames.FirstOrDefault() is not { } frame) return new(MetadataReadStatus.Corrupt, ErrorCode: "no-frame");
                if (frame.Metadata is BitmapMetadata metadata && !string.IsNullOrWhiteSpace(metadata.DateTaken))
                    return Parse(metadata.DateTaken);
                return malformed ? new(MetadataReadStatus.Corrupt, ErrorCode: "invalid-metadata") : new(MetadataReadStatus.Absent);
            }
            catch (NotSupportedException)
            {
                return metadataRead && !malformed ? new(MetadataReadStatus.Absent) :
                    new(MetadataReadStatus.Unsupported, ErrorCode: "unsupported-format");
            }
            catch (FileFormatException) { return new(MetadataReadStatus.Corrupt, ErrorCode: "invalid-image"); }
            catch (COMException) { return new(MetadataReadStatus.TransientError, ErrorCode: "wic-read-error"); }
        }
        catch (UnauthorizedAccessException) { return new(MetadataReadStatus.TransientError, ErrorCode: "access-denied"); }
        catch (IOException) { return new(MetadataReadStatus.TransientError, ErrorCode: "io-error"); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        { return new(MetadataReadStatus.Corrupt, ErrorCode: "invalid-metadata"); }
    }

    private static CaptureDateReadResult Parse(string value)
    {
        string[] formats = ["yyyy:MM:dd HH:mm:ss", "MM/dd/yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"];
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return new(MetadataReadStatus.Found, DateTime.SpecifyKind(date, DateTimeKind.Unspecified));
        return new(MetadataReadStatus.Corrupt, ErrorCode: "invalid-capture-date");
    }
}
