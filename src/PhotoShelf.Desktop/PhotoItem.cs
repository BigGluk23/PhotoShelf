using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace PhotoShelf.Desktop;

public sealed class PhotoItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isFavorite;
    private string? _metadataText;

    public PhotoItem(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        IsVideo = IsVideoPath(path);

        var file = new FileInfo(path);
        FileSizeBytes = file.Exists ? file.Length : 0;
        FileModifiedAt = file.Exists ? file.LastWriteTime : null;
    }

    public PhotoItem(string path, long sizeBytes, DateTime? modifiedAt)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        IsVideo = IsVideoPath(path);
        FileSizeBytes = sizeBytes;
        FileModifiedAt = modifiedAt;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public long ViewIndex { get; set; } = -1;

    public string FileName { get; }

    public bool IsVideo { get; }

    public bool IsPhoto => !IsVideo;

    public string KindLabel => IsVideo ? "VIDEO" : "";

    public string MediaTypeLabel => IsVideo ? "Видео" : "Фото";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }

            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteGlyph)));
        }
    }

    public string FavoriteGlyph => IsFavorite ? "★" : "";

    public long FileSizeBytes { get; }

    public DateTime? FileModifiedAt { get; }

    public DateTime? CaptureDate { get; private set; }

    public bool IsCaptureDateLoaded { get; private set; }

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    public string DetailLine
    {
        get
        {
            var size = FileSizeBytes > 0 ? $"{FileSizeBytes / 1024d / 1024d:0.0} MB" : "";
            var displayDate = CaptureDate ?? FileModifiedAt;
            var date = displayDate is null ? "" : displayDate.Value.ToString("dd.MM.yyyy");
            return string.Join("  ", new[] { date, size }.Where(static part => part.Length > 0));
        }
    }

    public string MetadataText => _metadataText ??= BuildMetadataText();

    public bool TryLoadCaptureDate()
    {
        if (IsCaptureDateLoaded)
        {
            return false;
        }

        IsCaptureDateLoaded = true;
        var captureDate = IsVideo ? null : ReadCaptureDate(Path);
        if (CaptureDate == captureDate)
        {
            return false;
        }

        CaptureDate = captureDate;
        _metadataText = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CaptureDate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailLine)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetadataText)));
        return true;
    }

    public void ApplyIndexedCaptureDate(DateTime? captureDate)
    {
        IsCaptureDateLoaded = true;
        CaptureDate = captureDate;
        _metadataText = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CaptureDate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailLine)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetadataText)));
    }

    public static bool IsSupported(string path)
    {
        return IsPhotoPath(path) || IsVideoPath(path);
    }

    public static bool IsPhotoPath(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".hif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".avif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jxl", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jp2", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".j2k", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".psd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dng", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cr2", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cr3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".nef", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".nrw", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".arw", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".srf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sr2", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".raf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".orf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".rw2", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pef", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".x3f", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".erf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".kdc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".rwl", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".raw", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVideoPath(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".avi", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mts", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m2ts", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".3gp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".3g2", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ts", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m2v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vob", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ogv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".flv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".f4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".asf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".divx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".rm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".rmvb", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".hevc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".prores", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAnimatedGifPath(string path)
    {
        return System.IO.Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildMetadataText()
    {
        var text = new StringBuilder();
        text.AppendLine(FileName);
        text.AppendLine();
        text.AppendLine($"Тип: {MediaTypeLabel}");
        text.AppendLine($"Размер файла: {FileSizeBytes / 1024d / 1024d:0.0} MB");
        if (FileModifiedAt is not null)
        {
            text.AppendLine($"Дата файла: {FileModifiedAt:dd.MM.yyyy HH:mm:ss}");
        }
        text.AppendLine(CaptureDate is null
            ? IsCaptureDateLoaded ? "Дата съёмки: не найдена" : "Дата съёмки: ещё не индексировалась"
            : $"Дата съёмки: {CaptureDate:dd.MM.yyyy HH:mm:ss}");

        text.AppendLine($"Папка: {Folder}");
        text.AppendLine($"Путь: {Path}");

        if (IsVideo)
        {
            text.AppendLine();
            text.AppendLine("Метаданные видео: пока базовые. Кадры и длительность добавим через видеодекодер.");
            return text.ToString();
        }

        try
        {
            var info = SixLabors.ImageSharp.Image.Identify(Path);
            text.AppendLine($"Пиксели: {info.Width} x {info.Height}");
            text.AppendLine($"Кадров: {info.FrameMetadataCollection.Count}");
            if (info.Metadata.ExifProfile is { } exif)
                foreach (var value in exif.Values) text.AppendLine($"{value.Tag}: {value.GetValue()}");
            if (info.Metadata.IptcProfile is { } iptc)
                foreach (var value in iptc.Values) text.AppendLine($"IPTC {value.Tag}: {value.Value}");
            if (info.Metadata.XmpProfile is { } xmp)
                text.AppendLine(System.Text.Encoding.UTF8.GetString(xmp.ToByteArray()));
            return text.ToString();
        }
        catch { /* A WIC metadata reader remains a fallback for legacy formats. */ }

        try
        {
            using var stream = File.OpenRead(Path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame is null)
            {
                return text.ToString();
            }

            text.AppendLine();
            text.AppendLine("Изображение:");
            text.AppendLine($"Пиксели: {frame.PixelWidth} x {frame.PixelHeight}");
            text.AppendLine($"DPI: {frame.DpiX:0} x {frame.DpiY:0}");

            if (frame.Metadata is BitmapMetadata metadata)
            {
                AppendIfPresent(text, "Дата съемки", metadata.DateTaken);
                AppendIfPresent(text, "Камера", Join(metadata.CameraManufacturer, metadata.CameraModel));
                AppendIfPresent(text, "Автор", metadata.Author is null ? null : string.Join(", ", metadata.Author));
                AppendIfPresent(text, "Название", metadata.Title);
                AppendIfPresent(text, "Тема", metadata.Subject);
                AppendIfPresent(text, "Комментарий", metadata.Comment);
                AppendIfPresent(text, "Copyright", metadata.Copyright);
                if (metadata.Keywords is { Count: > 0 })
                {
                    AppendIfPresent(text, "Ключевые слова", string.Join(", ", metadata.Keywords));
                }

                if (metadata.Rating > 0)
                {
                    text.AppendLine($"Рейтинг: {metadata.Rating}");
                }
            }
        }
        catch
        {
            text.AppendLine();
            text.AppendLine("Метаданные: Windows не смог прочитать этот формат без дополнительного декодера.");
        }

        return text.ToString();
    }

    private static void AppendIfPresent(StringBuilder text, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            text.AppendLine($"{label}: {value}");
        }
    }

    private static string? Join(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.IsNullOrWhiteSpace(second) ? null : second;
        }

        return string.IsNullOrWhiteSpace(second) ? first : $"{first} {second}";
    }

    public static DateTime? ReadCaptureDate(string path)
    {
        try
        {
            var exif = SixLabors.ImageSharp.Image.Identify(path).Metadata.ExifProfile;
            if (exif is not null && exif.TryGetValue(ExifTag.DateTimeOriginal, out var original) &&
                DateTime.TryParseExact(original.Value, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var capture))
                return capture;
        }
        catch { }
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            if (decoder.Frames.FirstOrDefault()?.Metadata is not BitmapMetadata metadata ||
                string.IsNullOrWhiteSpace(metadata.DateTaken))
            {
                return null;
            }

            var formats = new[]
            {
                "MM/dd/yyyy HH:mm:ss",
                "yyyy:MM:dd HH:mm:ss",
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-ddTHH:mm:ss"
            };

            if (DateTime.TryParseExact(metadata.DateTaken, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var exact))
            {
                return exact;
            }

            return DateTime.TryParse(metadata.DateTaken, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed)
                ? parsed
                : null;
        }
        catch
        {
            return null;
        }
    }
}
