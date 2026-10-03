using System.Diagnostics;
using System.Text.Json;

namespace PhotoShelf.Application.Updates.Installation;

/// <summary>
/// A normal entry point keeps this lease until it owns the catalog single-writer mutex. The
/// updater keeps it from handoff until startup health, admitting only its authenticated successor.
/// File existence is never ownership: the operating system releases handles after a process crash.
/// </summary>
public sealed class UpdateStartupLease : IDisposable
{
    private FileStream? _entryStream;
    private FileStream? _installationStream;
    private UpdateStartupLease(FileStream? entryStream, FileStream? installationStream)
    { _entryStream = entryStream; _installationStream = installationStream; }

    public static UpdateStartupLease? TryAcquire(UpdateInstallationPaths paths, string publicKeyPem,
        string currentVersion, string executablePath)
    {
        FileStream entry;
        try { entry = AcquireExclusive(paths, paths.StartupEntryLockPath); }
        catch (IOException error) when (IsSharingViolation(error)) { return null; }
        try
        {
            try { return new(entry, AcquireExclusive(paths, paths.InstallationLockPath)); }
            catch (IOException error) when (IsSharingViolation(error))
            {
                // Keep entry ownership even for the helper's successor. Otherwise a candidate
                // suspended here could resume into another installation after this helper exits.
                if (IsConsentedSuccessor(paths, publicKeyPem, currentVersion, executablePath))
                    return new(entry, null);
                entry.Dispose();
                return null;
            }
        }
        catch { entry.Dispose(); throw; }
    }

    internal static UpdateStartupLease AcquireForInstallation(UpdateInstallationPaths paths)
    {
        // Every participant acquires entry before installation. Never hold entry while waiting
        // for parent exit or health; the authenticated successor needs it to claim the catalog.
        using var entry = AcquireExclusive(paths, paths.StartupEntryLockPath);
        return new(null, AcquireExclusive(paths, paths.InstallationLockPath));
    }

    private static FileStream AcquireExclusive(UpdateInstallationPaths paths, string lockPath)
    {
        UpdateInstallationPaths.CreateDirectory(paths.ProgramRoot);
        UpdateInstallationPaths.RejectLinks(lockPath, allowMissing: true);
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static bool IsConsentedSuccessor(UpdateInstallationPaths paths, string publicKeyPem,
        string currentVersion, string executablePath)
    {
        var requestId = Environment.GetEnvironmentVariable(UpdateStartupHealth.RequestVariable);
        var installationId = Environment.GetEnvironmentVariable(UpdateStartupHealth.InstallationVariable);
        if (!Guid.TryParseExact(requestId, "N", out _) || !Guid.TryParseExact(installationId, "N", out _)) return false;
        try
        {
            var operation = paths.OperationDirectory(requestId!);
            var ready = UpdateInstallationPaths.ReadJson<UpdateHandoffRecord>(Path.Combine(operation, "helper-ready.json"));
            var accepted = UpdateInstallationPaths.ReadJson<UpdateHandoffRecord>(Path.Combine(operation, "parent-accepted.json"));
            var consumed = UpdateInstallationPaths.ReadJson<UpdateHandoffRecord>(Path.Combine(operation, "helper-consumed.json"));
            var committed = UpdateInstallationPaths.ReadJson<UpdateHandoffRecord>(Path.Combine(operation, "parent-committed.json"));
            if (ready != accepted || ready != consumed || ready != committed || ready.RequestId != requestId || !ready.IsValid ||
                !IsProcessAlive(ready.HelperProcessId, ready.HelperStartTimeUtcTicks)) return false;
            var claimPath = Path.Combine(operation, "request-claimed.json");
            // The helper writes this immutable claim once and holds its exclusive handle until before
            // the startup gate is released. A retained file alone cannot authorize a later launch.
            if (!UpdateVersion.TryParse(currentVersion, out var runningVersion) ||
                !UpdateVersion.TryParse(ready.ExpectedVersion, out var expectedVersion) || runningVersion != expectedVersion ||
                !IsClaimHeld(claimPath)) return false;
            var target = ActiveInstallationResolver.Resolve(paths, publicKeyPem);
            if (target is null || target.InstallationId != installationId ||
                !UpdateVersion.TryParse(target.Version, out var activeVersion) || activeVersion != runningVersion ||
                !string.Equals(Path.GetFullPath(executablePath), target.ExecutablePath, UpdateInstallationPaths.PathComparison)) return false;
            var directory = paths.InstallationDirectory(target.InstallationId);
            var release = UpdateManifestVerifier.Verify(
                UpdateInstallationPaths.ReadBounded(Path.Combine(directory, "photoshelf-update.json"), UpdateManifestVerifier.MaximumManifestBytes),
                UpdateInstallationPaths.ReadBounded(Path.Combine(directory, "photoshelf-update.sig"), 1024), publicKeyPem);
            return release.Manifest.PackageSha256 == ready.ExpectedPackageSha256;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    internal static bool IsClaimHeld(string path)
    {
        UpdateInstallationPaths.RejectLinks(path);
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return false;
        }
        catch (IOException error) when (IsSharingViolation(error)) { return true; }
    }

    internal static bool IsProcessAlive(int processId, long startTimeUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTimeUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static bool IsSharingViolation(IOException error)
    {
        var code = error.HResult & 0xffff;
        return code is 32 or 33 || !OperatingSystem.IsWindows() && code is 11 or 35;
    }

    public void Dispose()
    {
        try { Interlocked.Exchange(ref _installationStream, null)?.Dispose(); }
        finally { Interlocked.Exchange(ref _entryStream, null)?.Dispose(); }
    }
}
