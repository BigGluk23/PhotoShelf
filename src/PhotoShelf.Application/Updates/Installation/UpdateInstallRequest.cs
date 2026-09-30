using System.Diagnostics;
using System.Text.Json;

namespace PhotoShelf.Application.Updates.Installation;

/// <summary>A single-use, recently consented installation request. It is never discovered or resumed automatically.</summary>
public sealed record UpdateInstallRequest(int ProtocolVersion, string RequestId, string StageDirectory,
    int ParentProcessId, long ParentStartTimeUtcTicks, DateTimeOffset ConsentedAtUtc, string CurrentVersion,
    string ExpectedVersion, string ExpectedPackageSha256)
{
    public static Task<string> CreateFileAsync(StagedUpdate stage, UpdateInstallationPaths paths,
        string currentVersion, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateInstallationPaths.RequireDescendant(stage.StageDirectory, paths.StagingRoot);
        UpdateInstallationPaths.RejectLinks(stage.StageDirectory);
        if (!UpdateVersion.IsNewer(stage.Release.Version, currentVersion))
            throw new InvalidDataException("Only a newer signed update can be installed.");
        using var parent = Process.GetCurrentProcess();
        var request = new UpdateInstallRequest(1, Guid.NewGuid().ToString("N"), Path.GetFullPath(stage.StageDirectory),
            parent.Id, parent.StartTime.ToUniversalTime().Ticks, DateTimeOffset.UtcNow, currentVersion,
            stage.Release.Version, stage.Release.Manifest.PackageSha256);
        UpdateInstallationPaths.CreateDirectory(paths.RequestsRoot);
        var file = Path.Combine(paths.RequestsRoot, request.RequestId + ".json");
        UpdateInstallationPaths.WriteNewDurably(file, JsonSerializer.SerializeToUtf8Bytes(request, UpdateManifestVerifier.JsonOptions));
        return file;
    }, cancellationToken);

    internal static UpdateInstallRequest Read(string path, UpdateInstallationPaths paths, DateTimeOffset now)
    {
        UpdateInstallationPaths.RequireDescendant(path, paths.RequestsRoot);
        var request = UpdateInstallationPaths.ReadJson<UpdateInstallRequest>(path);
        UpdateInstallationPaths.RequireId(request.RequestId);
        if (!string.Equals(Path.GetFullPath(path), Path.Combine(paths.RequestsRoot, request.RequestId + ".json"),
                UpdateInstallationPaths.PathComparison) || request.ProtocolVersion != 1 ||
            request.ParentProcessId <= 0 || request.ParentStartTimeUtcTicks <= 0 ||
            request.ParentStartTimeUtcTicks > now.UtcTicks ||
            request.ConsentedAtUtc > now.AddMinutes(1) || request.ConsentedAtUtc < now.AddMinutes(-30) ||
            !UpdateVersion.IsNewer(request.ExpectedVersion, request.CurrentVersion) ||
            !UpdateManifestVerifier.IsSha256(request.ExpectedPackageSha256))
            throw new InvalidDataException("The installation request is invalid or its explicit consent has expired.");
        UpdateInstallationPaths.RequireDescendant(request.StageDirectory, paths.StagingRoot);
        UpdateInstallationPaths.RejectLinks(request.StageDirectory);
        return request;
    }
}
