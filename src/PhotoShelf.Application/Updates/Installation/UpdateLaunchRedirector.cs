using System.Diagnostics;

namespace PhotoShelf.Application.Updates.Installation;

/// <summary>Preserves the normal-start shortcut policy without discovering or activating pending requests.</summary>
public static class UpdateLaunchRedirector
{
    public static bool TryLaunchNewer(UpdateInstallationPaths paths, string publicKeyPem, string currentVersion,
        IDisposable? startupLease = null)
    {
        var active = ActiveInstallationResolver.Resolve(paths, publicKeyPem);
        if (active is null || !UpdateVersion.IsNewer(active.Version, currentVersion)) return false;
        // This launcher will never open a catalog. Release before starting the successor so its
        // normal admission cannot race the old launcher's still-open startup handle.
        startupLease?.Dispose();
        using var process = Process.Start(new ProcessStartInfo(active.ExecutablePath)
        { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(active.ExecutablePath)! })
            ?? throw new IOException("Не удалось запустить установленную версию PhotoShelf.");
        return true;
    }
}
