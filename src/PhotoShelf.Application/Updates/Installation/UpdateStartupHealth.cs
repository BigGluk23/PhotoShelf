using System.Text.Json;

namespace PhotoShelf.Application.Updates.Installation;

public sealed record UpdateStartupHealthRecord(int ProtocolVersion, string RequestId, string InstallationId,
    string State, DateTimeOffset ReadyAtUtc);

public static class UpdateStartupHealth
{
    public const string RequestVariable = "PHOTOSHELF_UPDATE_REQUEST_ID";
    public const string InstallationVariable = "PHOTOSHELF_UPDATE_INSTALLATION_ID";

    /// <summary>Call after real catalog startup and the first projection. This is evidence, never permission to roll back data.</summary>
    public static void ReportReady(string executablePath, UpdateInstallationPaths? paths = null)
    {
        var requestId = Environment.GetEnvironmentVariable(RequestVariable);
        var installationId = Environment.GetEnvironmentVariable(InstallationVariable);
        if (string.IsNullOrEmpty(requestId) && string.IsNullOrEmpty(installationId)) return;
        UpdateInstallationPaths.RequireId(requestId!);
        UpdateInstallationPaths.RequireId(installationId!);
        paths ??= new UpdateInstallationPaths();
        var expectedExecutable = Path.Combine(paths.InstallationDirectory(installationId!), "app", "PhotoShelf.exe");
        if (!string.Equals(Path.GetFullPath(executablePath), expectedExecutable, UpdateInstallationPaths.PathComparison))
            throw new InvalidDataException("The startup health report does not belong to this installation.");
        var pointer = UpdateInstallationPaths.ReadJson<ActiveInstallationPointer>(paths.ActivePointerPath);
        if (pointer.ProtocolVersion != 1 || pointer.InstallationId != installationId)
            throw new InvalidDataException("The active installation changed before startup completed.");
        var operation = paths.OperationDirectory(requestId!);
        UpdateInstallationPaths.RejectLinks(operation);
        var record = new UpdateStartupHealthRecord(1, requestId!, installationId!, "ready", DateTimeOffset.UtcNow);
        var path = Path.Combine(operation, "startup-ready.json");
        // The helper must never observe a final receipt while it is still being written. Retain any
        // interrupted pending file as diagnostic evidence; never replace a receipt from another launch.
        var pending = Path.Combine(operation, "startup-ready.pending-" + Guid.NewGuid().ToString("N") + ".json");
        UpdateInstallationPaths.WriteNewDurably(pending, JsonSerializer.SerializeToUtf8Bytes(record, UpdateManifestVerifier.JsonOptions));
        File.Move(pending, path, overwrite: false);
        Environment.SetEnvironmentVariable(RequestVariable, null);
        Environment.SetEnvironmentVariable(InstallationVariable, null);
        // The final health receipt is durable and the installed copy no longer reads staging.
        // Cleanup is best effort and strictly limited to the stage named by this consumed request.
        UpdateStorageCleanup.TryCleanupConfirmedRequest(paths, requestId!);
    }
}
