using System.IO;

namespace PhotoShelf.Desktop;

public sealed record PhotoScanBatch(string CurrentFolder, IReadOnlyList<string> Paths, int SeenCount);

public static class PhotoScanner
{
    private const int BatchSize = 24;

    public static bool IsIgnoredPath(string path, bool includeSystemFolders = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var normalized = Path.GetFullPath(path);
        if (QuarantineConfiguration.IsKnownQuarantine(normalized)) return true;
        var appCatalog = Path.GetFullPath(LocalCatalogStore.CatalogDirectory);
        if (normalized.StartsWith(appCatalog, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (includeSystemFolders)
        {
            return normalized.Contains(
                $"{Path.DirectorySeparatorChar}$Recycle.Bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase);
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows) &&
            normalized.StartsWith(Path.GetFullPath(windows), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var programFolder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                 })
        {
            if (!string.IsNullOrWhiteSpace(programFolder) &&
                normalized.StartsWith(Path.GetFullPath(programFolder), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            if (normalized.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains($"{Path.DirectorySeparatorChar}Cache{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains($"{Path.DirectorySeparatorChar}Temp{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return normalized.Contains(
            $"{Path.DirectorySeparatorChar}$Recycle.Bin{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<string> GetDefaultDiscoveryRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            seen.Add(drive.RootDirectory.FullName);
        }

        AddKnownFolder(Environment.SpecialFolder.MyPictures, seen);
        AddKnownFolder(Environment.SpecialFolder.DesktopDirectory, seen);
        AddKnownFolder(Environment.SpecialFolder.UserProfile, seen, "Downloads");
        AddKnownFolder(Environment.SpecialFolder.MyDocuments, seen);

        return seen;
    }

    public static Task ScanAsync(IEnumerable<string> roots, Func<PhotoScanBatch, Task> apply,
        bool includeSystemFolders, CancellationToken cancellationToken, string[]? excluded = null, bool recursive = true)
    {
        return Task.Run(async () =>
        {
            var pending = new Queue<string>(roots.Distinct(StringComparer.OrdinalIgnoreCase));
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var batch = new List<string>(64);
            var seen = 0;
            bool Excluded(string path) => excluded?.Any(x => path.Equals(x, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(x.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) == true;
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | (includeSystemFolders ? 0 : FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary) };
            while (pending.TryDequeue(out var folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visited.Add(folder) || Excluded(folder) || IsIgnoredPath(folder, includeSystemFolders)) continue;
                try
                {
                    foreach (var path in Directory.EnumerateFiles(folder, "*", options))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!PhotoItem.IsSupported(path) || IsIgnoredPath(path, includeSystemFolders)) continue;
                        batch.Add(path); seen++;
                        if (batch.Count < 64) continue;
                        await apply(new PhotoScanBatch(folder, batch.ToArray(), seen)).ConfigureAwait(false);
                        batch.Clear();
                    }
                    if (recursive) foreach (var child in Directory.EnumerateDirectories(folder, "*", options)) pending.Enqueue(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (batch.Count > 0)
                {
                    await apply(new PhotoScanBatch(folder, batch.ToArray(), seen)).ConfigureAwait(false);
                    batch.Clear();
                }
            }
        }, cancellationToken);
    }

    private static void AddKnownFolder(Environment.SpecialFolder folder, ISet<string> paths, string? child = null)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        paths.Add(child is null ? path : Path.Combine(path, child));
    }
}
