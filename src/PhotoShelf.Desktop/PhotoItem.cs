using PhotoShelf.Application.Metadata;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MetadataExtractor;
using MetadataExtractor.Formats.Xmp;

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

    public long FileSizeBytes { get; private set; }

    public DateTime? FileModifiedAt { get; private set; }

    public DateTime? CaptureDate { get; private set; }

    public bool IsCaptureDateLoaded { get; private set; }
    public MetadataReadStatus MetadataStatus { get; private set; }

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

        var result = IsVideo ? new CaptureDateReadResult(MetadataReadStatus.Unsupported) : CaptureDateReader.Read(Path);
        var date = result.ApplyTo(CaptureDate);
        var changed = CaptureDate != date;
        ApplyIndexedCaptureDate(date, result.Status);
        return changed;
    }

    public void ApplyIndexedCaptureDate(DateTime? captureDate, MetadataReadStatus? status = null)
    {
        var readStatus = status ?? (captureDate is null ? MetadataReadStatus.Absent : MetadataReadStatus.Found);
        if (CaptureDate == captureDate && MetadataStatus == readStatus &&
            IsCaptureDateLoaded == (readStatus != MetadataReadStatus.Pending)) return;
        IsCaptureDateLoaded = readStatus != MetadataReadStatus.Pending;
        MetadataStatus = readStatus;
        CaptureDate = captureDate;
        _metadataText = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CaptureDate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailLine)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetadataText)));
    }

    public void ApplyFileInformation(long sizeBytes, DateTime? modifiedAt)
    {
        if (FileSizeBytes == sizeBytes && FileModifiedAt == modifiedAt) return;
        FileSizeBytes = sizeBytes; FileModifiedAt = modifiedAt;
        CaptureDate = null; IsCaptureDateLoaded = false; MetadataStatus = MetadataReadStatus.Pending; _metadataText = null;
        foreach (var name in new[] { nameof(FileSizeBytes), nameof(FileModifiedAt), nameof(CaptureDate), nameof(DetailLine), nameof(MetadataText) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static bool IsSupported(string path)
    {
        return IsPhotoPath(path) || IsVideoPath(path);
    }

    public static bool IsPhotoPath(string path) => PhotoShelf.Domain.MediaFormatRegistry.IsPhoto(path);

    public static bool IsVideoPath(string path) => PhotoShelf.Domain.MediaFormatRegistry.IsVideo(path);

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
            ? MetadataStatus == MetadataReadStatus.Absent ? "Дата съёмки: не найдена" : MetadataStatus == MetadataReadStatus.Pending ? "Дата съёмки: ещё не индексировалась" : "Дата съёмки: не прочитана"
            : $"Дата съёмки: {CaptureDate:dd.MM.yyyy HH:mm:ss}");

        if (MetadataStatus is MetadataReadStatus.TransientError or MetadataReadStatus.Corrupt or MetadataReadStatus.Unsupported)
            text.AppendLine(MetadataStatus switch
            {
                MetadataReadStatus.TransientError => "Последнее чтение: временная ошибка; повтор в фоне. Ранее найденная дата сохранена.",
                MetadataReadStatus.Corrupt => "Последнее чтение: повреждённые метаданные. Ранее найденная дата сохранена.",
                _ => "Последнее чтение: формат пока не поддерживается. Ранее найденная дата сохранена."
            });
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
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var directories = ImageMetadataReader.ReadMetadata(stream, Path);
            foreach (var directory in directories)
            {
                text.AppendLine($"[{directory.Name}]");
                foreach (var tag in directory.Tags)
                {
                    text.AppendLine($"{tag.Name}: {tag.Description}");
                    if (text.Length > 64 * 1024) { text.AppendLine("Показаны первые 64 КиБ метаданных."); return text.ToString(); }
                }
                if (directory is XmpDirectory xmp)
                    foreach (var value in xmp.GetXmpProperties())
                    {
                        text.AppendLine($"XMP {value.Key}: {value.Value}");
                        if (text.Length > 64 * 1024) { text.AppendLine("Показаны первые 64 КиБ метаданных."); return text.ToString(); }
                    }
                if (directory.Errors.Any()) text.AppendLine("Часть метаданных повреждена или не прочитана.");
            }
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

    public static DateTime? ReadCaptureDate(string path) => CaptureDateReader.Read(path).CaptureDate;
}
