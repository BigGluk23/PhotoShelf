using System.Text.Json;

namespace PhotoShelf.Application.Updates.Installation;

/// <summary>
/// Best-effort garbage collection for PhotoShelf-owned update transport files. Unknown names,
/// links, operation journals, installed versions, catalogs and media are deliberately untouched.
/// </summary>
public static class UpdateStorageCleanup
{
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(31);

    public static void CleanupAtStartup(string stagingRoot, string? preservedStageId,
        string? requestsRoot = null, string? preservedRequestId = null, DateTimeOffset? now = null,
        string? operationsRoot = null)
    {
        // A successor may still be opening its catalog. Its version number is not startup health.
        if (!string.IsNullOrEmpty(preservedRequestId)) return;
        var stages = new HashSet<string>(StringComparer.Ordinal);
        var requests = new HashSet<string>(StringComparer.Ordinal);
        if (NormalizeId(preservedStageId) is { } stageId) stages.Add(stageId);
        // After a crash, environment variables are gone; durable claims still protect transport.
        if (!CollectUnconfirmedInstallations(stagingRoot, operationsRoot, stages, requests)) return;
        CleanupStages(stagingRoot, stages);
        if (!string.IsNullOrWhiteSpace(requestsRoot))
            CleanupExpiredRequests(requestsRoot, requests, now ?? DateTimeOffset.UtcNow);
    }

    private static bool CollectUnconfirmedInstallations(string stagingRoot, string? operationsRoot,
        HashSet<string> stages, HashSet<string> requests)
    {
        if (operationsRoot is null) return true;
        try
        {
            UpdateInstallationPaths.RejectLinks(operationsRoot, allowMissing: true);
            if (!Directory.Exists(operationsRoot)) return true;
            foreach (var operation in Directory.EnumerateDirectories(operationsRoot))
            {
                var requestId = NormalizeId(Path.GetFileName(operation));
                if (requestId is null) continue;
                UpdateInstallationPaths.RejectLinks(operation);
                UpdateInstallRequest request;
                try { request = UpdateInstallationPaths.ReadJson<UpdateInstallRequest>(Path.Combine(operation, "request-claimed.json")); }
                catch (FileNotFoundException) { continue; }
                if (request.ProtocolVersion != 1 || request.RequestId != requestId ||
                    !IsImmediateGuidChild(request.StageDirectory, stagingRoot, out var stage)) return false;
                var confirmed = false;
                try
                {
                    var health = UpdateInstallationPaths.ReadJson<UpdateStartupHealthRecord>(Path.Combine(operation, "startup-ready.json"));
                    confirmed = health.ProtocolVersion == 1 && health.RequestId == requestId &&
                        health.State == "ready" && NormalizeId(health.InstallationId) is not null;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                    InvalidDataException or JsonException or ArgumentException or NotSupportedException) { }
                if (confirmed) continue;
                stages.Add(Path.GetFileName(stage));
                requests.Add(requestId);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or ArgumentException or NotSupportedException)
        {
            // Unreadable/unfinished claims never grant permission to discard their transport.
            return false;
        }
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
            var viewPath = Path.Combine(paths.RequestsRoot, requestId + ".view.json");
            UpdateInstallationPaths.RejectLinks(viewPath, allowMissing: true);
            File.Delete(viewPath);
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

    private static void CleanupStages(string stagingRoot, HashSet<string> preservedStages)
    {
        try
        {
            var root = Path.GetFullPath(stagingRoot);
            if (!Directory.Exists(root)) return;
            UpdateInstallationPaths.RejectLinks(root);
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                var id = NormalizeId(Path.GetFileName(directory));
                if (id is null || preservedStages.Contains(id)) continue;
                TryDeleteOwnedStage(directory, root);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or NotSupportedException)
        {
            // Cleanup must never block startup or turn an uncertain path into deletion authority.
        }
    }

    private static void CleanupExpiredRequests(string requestsRoot, HashSet<string> preservedRequests, DateTimeOffset now)
    {
        try
        {
            var root = Path.GetFullPath(requestsRoot);
            if (!Directory.Exists(root)) return;
            UpdateInstallationPaths.RejectLinks(root);
            foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
            {
                var id = OwnedRequestArtifactId(Path.GetFileName(file));
                if (id is null || preservedRequests.Contains(id)) continue;
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

    private static string? OwnedRequestArtifactId(string fileName)
    {
        const string viewSuffix = ".view.json";
        const string requestSuffix = ".json";
        var id = fileName.EndsWith(viewSuffix, StringComparison.Ordinal) ? fileName[..^viewSuffix.Length] :
            fileName.EndsWith(requestSuffix, StringComparison.Ordinal) ? fileName[..^requestSuffix.Length] : null;
        return NormalizeId(id);
    }

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
