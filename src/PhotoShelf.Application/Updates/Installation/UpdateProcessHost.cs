using System.Diagnostics;
using System.Text.Json;

namespace PhotoShelf.Application.Updates.Installation;

public sealed record UpdateApplicationLaunch(string ExecutablePath, string? RequestId = null, string? InstallationId = null);

public interface IUpdateProcessHost
{
    Task WaitForParentExitAsync(int processId, long startTimeUtcTicks, TimeSpan timeout, CancellationToken cancellationToken);
    Task VerifyStartupAsync(string executablePath, string reportPath, TimeSpan timeout, CancellationToken cancellationToken);
    void Launch(UpdateApplicationLaunch application);
}

/// <summary>All waits are bounded. Neither the previous app nor the candidate is forcibly terminated.</summary>
public sealed class UpdateProcessHost : IUpdateProcessHost
{
    public async Task WaitForParentExitAsync(int processId, long startTimeUtcTicks, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; } // The explicitly identified parent already exited.
        using (process)
        {
            try
            {
                if (process.StartTime.ToUniversalTime().Ticks != startTimeUtcTicks)
                    throw new InvalidOperationException("The parent process identity changed. Installation was not started.");
            }
            catch (InvalidOperationException) when (process.HasExited) { return; }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(timeout);
            try { await process.WaitForExitAsync(wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("PhotoShelf is still closing. No process was terminated and no installation was activated."); }
        }
    }

    public async Task VerifyStartupAsync(string executablePath, string reportPath, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        UpdateInstallationPaths.RejectLinks(executablePath);
        var start = new ProcessStartInfo(executablePath) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)! };
        start.ArgumentList.Add("--verify-startup-resources");
        start.ArgumentList.Add("--startup-report");
        start.ArgumentList.Add(reportPath);
        start.Environment.Remove(UpdateStartupHealth.RequestVariable);
        start.Environment.Remove(UpdateStartupHealth.InstallationVariable);
        using var process = Process.Start(start) ?? throw new IOException("The isolated startup verification process could not be started.");
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        try { await process.WaitForExitAsync(wait.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The isolated startup check has not finished. The previous installation remains active."); }
        if (process.ExitCode != 0) throw new InvalidDataException("The candidate failed isolated startup verification.");
        using var report = JsonDocument.Parse(UpdateInstallationPaths.ReadBounded(reportPath, 64 * 1024));
        if (!report.RootElement.TryGetProperty("status", out var status) || status.GetString() != "passed" ||
            !report.RootElement.TryGetProperty("check", out var check) || check.GetString() != "startup-resources")
            throw new InvalidDataException("The isolated startup check did not produce a successful report.");
    }

    public void Launch(UpdateApplicationLaunch application)
    {
        UpdateInstallationPaths.RejectLinks(application.ExecutablePath);
        var start = new ProcessStartInfo(application.ExecutablePath) { UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(application.ExecutablePath)! };
        start.Environment.Remove(UpdateStartupHealth.RequestVariable);
        start.Environment.Remove(UpdateStartupHealth.InstallationVariable);
        if (application.RequestId is not null && application.InstallationId is not null)
        {
            start.Environment[UpdateStartupHealth.RequestVariable] = UpdateInstallationPaths.RequireId(application.RequestId);
            start.Environment[UpdateStartupHealth.InstallationVariable] = UpdateInstallationPaths.RequireId(application.InstallationId);
        }
        using var process = Process.Start(start) ?? throw new IOException("The activated application could not be started.");
    }
}
