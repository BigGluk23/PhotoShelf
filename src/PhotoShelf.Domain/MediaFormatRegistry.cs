namespace PhotoShelf.Domain;

/// <summary>One classification for catalog eligibility and safe companion discovery; eligibility does not promise a decoder.</summary>
public static class MediaFormatRegistry
{
    private static readonly HashSet<string> Raw = new(StringComparer.OrdinalIgnoreCase)
    { ".raw", ".dng", ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".orf", ".rw2", ".raf", ".pef", ".srw", ".x3f", ".erf", ".kdc", ".rwl" };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".hif", ".avif", ".jxl", ".jp2", ".j2k", ".psd", ".svg" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm", ".wmv", ".mts", ".m2ts", ".3gp", ".3g2", ".mpg", ".mpeg", ".ts", ".m2v", ".vob", ".ogv", ".flv", ".f4v", ".asf", ".divx", ".dv", ".rm", ".rmvb", ".hevc", ".prores" };
    public static IReadOnlyCollection<string> RawExtensions { get; } = Array.AsReadOnly(Raw.Order(StringComparer.Ordinal).ToArray());
    public static IReadOnlyCollection<string> ImageExtensions { get; } = Array.AsReadOnly(Images.Order(StringComparer.Ordinal).ToArray());
    public static bool IsRaw(string path) => Raw.Contains(Path.GetExtension(path));
    public static bool IsPhoto(string path) => IsRaw(path) || Images.Contains(Path.GetExtension(path));
    public static bool IsVideo(string path) => Videos.Contains(Path.GetExtension(path));
    // TypeScript source/declaration files share these suffixes with MPEG transport streams.
    // Keep extension checks free of I/O; admission probes belong to background workers.
    public static bool HasAmbiguousVideoExtension(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ts" or ".mts";
    public static bool IsSidecar(string path) => Path.GetExtension(path).Equals(".xmp", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".aae", StringComparison.OrdinalIgnoreCase);
}
