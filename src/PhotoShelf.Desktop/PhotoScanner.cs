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

    public static Task ScanAsync(
        IEnumerable<string> roots,
        IProgress<PhotoScanBatch>? progress,
        bool includeSystemFolders,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.Temporary
            };
            var batch = new List<string>(BatchSize);
            var seenCount = 0;

            foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new PhotoScanBatch(root, Array.Empty<string>(), seenCount));

                try
                {
                    foreach (var path in Directory.EnumerateFiles(root, "*.*", options))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (IsIgnoredPath(path, includeSystemFolders) || !PhotoItem.IsSupported(path))
                        {
                            continue;
                        }

                        batch.Add(path);
                        seenCount++;

                        if (batch.Count >= BatchSize)
                        {
                            progress?.Report(new PhotoScanBatch(root, batch.ToArray(), seenCount));
                            batch.Clear();
                            Thread.Sleep(20);
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }

                if (batch.Count > 0)
                {
                    progress?.Report(new PhotoScanBatch(root, batch.ToArray(), seenCount));
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
