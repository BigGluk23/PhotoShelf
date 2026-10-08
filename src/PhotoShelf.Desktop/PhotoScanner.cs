using System.IO;
using PhotoShelf.Application.Catalog;

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
        if (IsInternalStoragePath(normalized, CatalogStoragePaths.InternalRoots))
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
            LibraryCatalogSynchronizer.IsUnder(normalized, windows))
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
                LibraryCatalogSynchronizer.IsUnder(normalized, programFolder))
            {
                return true;
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            if (LibraryCatalogSynchronizer.IsUnder(normalized, tempRoot))
            {
                return true;
            }
        }

        return normalized.Contains(
            $"{Path.DirectorySeparatorChar}$Recycle.Bin{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsInternalStoragePath(string path, params string[] storageRoots) =>
        storageRoots.Any(root => LibraryCatalogSynchronizer.IsUnder(path, root));

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
        bool includeSystemFolders, CancellationToken cancellationToken, string[]? excluded = null, bool recursive = true,
        FolderInclusionRules? inclusion = null, Func<CancellationToken, Task>? checkpoint = null)
    {
        var rules = inclusion?.Snapshot() ?? new FolderInclusionRules(excluded ?? []);
        return Task.Run(async () =>
        {
            var pending = new Queue<string>(roots.Distinct(StringComparer.OrdinalIgnoreCase));
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var batch = new List<string>(64);
            var seen = 0;
            var enumerated = 0;
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | (includeSystemFolders ? 0 : FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary) };
            while (pending.TryDequeue(out var folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (checkpoint is not null) await checkpoint(cancellationToken).ConfigureAwait(false);
                if (!visited.Add(folder) || !rules.MayContainIncluded(folder) || IsIgnoredPath(folder, includeSystemFolders)) continue;
                try
                {
                    if (rules.IsIncluded(folder)) foreach (var path in Directory.EnumerateFiles(folder, "*", options))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++enumerated % 64 == 0 && checkpoint is not null) await checkpoint(cancellationToken).ConfigureAwait(false);
                        if (!PhotoItem.IsSupported(path) || IsIgnoredPath(path, includeSystemFolders)) continue;
                        batch.Add(path); seen++;
                        if (batch.Count < 64) continue;
                        await apply(new PhotoScanBatch(folder, batch.ToArray(), seen)).ConfigureAwait(false);
                        batch.Clear();
                    }
                    if (recursive) foreach (var child in Directory.EnumerateDirectories(folder, "*", options))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++enumerated % 64 == 0 && checkpoint is not null) await checkpoint(cancellationToken).ConfigureAwait(false);
                        if (rules.MayContainIncluded(child)) pending.Enqueue(child);
                    }
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
