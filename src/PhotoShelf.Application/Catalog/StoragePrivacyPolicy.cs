namespace PhotoShelf.Application.Catalog;

public static class StoragePrivacyPolicy
{
    public static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalized.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string? KnownSynchronizationWarning(string path)
    {
        var segments = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            segments.Any(part => part.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Dropbox", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("Yandex.Disk", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("YandexDisk", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Google Drive", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Мой диск", StringComparison.OrdinalIgnoreCase)))
            return "Путь похож на сетевую или синхронизируемую папку. Внешняя программа может копировать её содержимое, включая оригиналы в карантине.";
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured && IsUnder(path, configured))
                return "Папка находится в настроенном OneDrive. Оригиналы в карантине могут синхронизироваться.";
        return null;
    }
}
