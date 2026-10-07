namespace PhotoShelf.Application.Updates.Installation;

/// <summary>
/// Best-effort garbage collection for PhotoShelf-owned update transport files. Unknown names,
/// links, operation journals, installed versions, catalogs and media are deliberately untouched.
/// </summary>
public static class UpdateStorageCleanup
{
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(31);

    public static void CleanupAtStartup(string stagingRoot, string? preservedStageId,
        string? requestsRoot = null, string? preservedRequestId = null, DateTimeOffset? now = null)
    {
        var keepStage = NormalizeId(preservedStageId);
        var keepRequest = NormalizeId(preservedRequestId);
        CleanupStages(stagingRoot, keepStage);
        if (!string.IsNullOrWhiteSpace(requestsRoot))
            CleanupExpiredRequests(requestsRoot, keepRequest, now ?? DateTimeOffset.UtcNow);
    }

    internal static bool TryCleanupConfirmedRequest(UpdateInstallationPaths paths, string requestId)
    {
        try
        {
            requestId = UpdateInstallationPaths.RequireId(requestId);
            var requestPath = Path.Combine(paths.RequestsRoot, requestId + ".json");
            var request = UpdateInstallationPaths.ReadJson<UpdateInstallRequest>(requestPath);
            if (request.ProtocolVersion != 1 || request.RequestId != requestId ||
                !IsImmediateGuidChild(request.StageDirectory, paths.StagingRoot, out _))
                return false;
            if (!TryDeleteOwnedStage(request.StageDirectory, paths.StagingRoot)) return false;
            UpdateInstallationPaths.RejectLinks(requestPath);
            File.Delete(requestPath);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool TryDeleteOwnedStage(string stageDirectory, string stagingRoot)
    {
        try
        {
            if (!IsImmediateGuidChild(stageDirectory, stagingRoot, out var fullStage)) return false;
            if (!Directory.Exists(fullStage)) return true;
            UpdateInstallationPaths.RejectLinks(fullStage);
            if (ContainsLink(fullStage)) return false;
            Directory.Delete(fullStage, recursive: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void CleanupStages(string stagingRoot, string? preservedStageId)
    {
        try
        {
            var root = Path.GetFullPath(stagingRoot);
            if (!Directory.Exists(root)) return;
            UpdateInstallationPaths.RejectLinks(root);
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                var id = NormalizeId(Path.GetFileName(directory));
                if (id is null || id == preservedStageId) continue;
                TryDeleteOwnedStage(directory, root);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or NotSupportedException)
        {
            // Cleanup must never block startup or turn an uncertain path into deletion authority.
        }
    }

    private static void CleanupExpiredRequests(string requestsRoot, string? preservedRequestId, DateTimeOffset now)
    {
        try
        {
            var root = Path.GetFullPath(requestsRoot);
            if (!Directory.Exists(root)) return;
            UpdateInstallationPaths.RejectLinks(root);
            foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
            {
                var id = NormalizeId(Path.GetFileNameWithoutExtension(file));
                if (id is null || id == preservedRequestId) continue;
                try
                {
                    UpdateInstallationPaths.RejectLinks(file);
                    if (new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) > now - RequestLifetime) continue;
                    File.Delete(file);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                    InvalidDataException or ArgumentException or NotSupportedException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or NotSupportedException) { }
    }

    private static bool IsImmediateGuidChild(string path, string root, out string fullPath)
    {
        fullPath = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return NormalizeId(Path.GetFileName(fullPath)) is not null &&
            string.Equals(Path.GetDirectoryName(fullPath), fullRoot, UpdateInstallationPaths.PathComparison);
    }

    private static string? NormalizeId(string? value) =>
        value is not null && Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value ? value : null;

    private static bool ContainsLink(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        return false;
    }
}
