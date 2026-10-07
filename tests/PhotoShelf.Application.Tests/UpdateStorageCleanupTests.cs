using System.Text.Json;
using PhotoShelf.Application.Updates;
using PhotoShelf.Application.Updates.Installation;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class UpdateStorageCleanupTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-update-cleanup-" + Guid.NewGuid().ToString("N"));
    private UpdateInstallationPaths Paths => new(_root);

    [Fact]
    public void StartupCleanupDeletesOnlyUnreferencedGuidStagesAndExpiredGuidRequests()
    {
        var kept = Guid.NewGuid().ToString("N");
        var orphan = Guid.NewGuid().ToString("N");
        var unknown = "manual-evidence";
        foreach (var name in new[] { kept, orphan, unknown })
        {
            var directory = Path.Combine(Paths.StagingRoot, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "evidence.txt"), name);
        }
        Directory.CreateDirectory(Paths.RequestsRoot);
        var expired = Path.Combine(Paths.RequestsRoot, Guid.NewGuid().ToString("N") + ".json");
        var recent = Path.Combine(Paths.RequestsRoot, Guid.NewGuid().ToString("N") + ".json");
        var unowned = Path.Combine(Paths.RequestsRoot, "notes.json");
        File.WriteAllText(expired, "{}"); File.WriteAllText(recent, "{}"); File.WriteAllText(unowned, "{}");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddHours(-1));

        UpdateStorageCleanup.CleanupAtStartup(Paths.StagingRoot, kept, Paths.RequestsRoot,
            preservedRequestId: null, new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));

        Assert.True(Directory.Exists(Path.Combine(Paths.StagingRoot, kept)));
        Assert.False(Directory.Exists(Path.Combine(Paths.StagingRoot, orphan)));
        Assert.True(Directory.Exists(Path.Combine(Paths.StagingRoot, unknown)));
        Assert.False(File.Exists(expired)); Assert.True(File.Exists(recent)); Assert.True(File.Exists(unowned));
    }

    [Fact]
    public void StartupReadyDeletesItsConfirmedStageAndRequestButRetainsOperationReceipt()
    {
        var stageId = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(Paths.StagingRoot, stageId);
        Directory.CreateDirectory(stage); File.WriteAllText(Path.Combine(stage, "package.zip"), "synthetic");
        Directory.CreateDirectory(Paths.RequestsRoot);
        var requestId = Guid.NewGuid().ToString("N");
        var request = new UpdateInstallRequest(1, requestId, stage, Environment.ProcessId,
            System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, DateTimeOffset.UtcNow,
            "1.11.1", "1.11.2", new string('a', 64));
        var requestPath = Path.Combine(Paths.RequestsRoot, requestId + ".json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request, JsonOptions));
        var installationId = Guid.NewGuid().ToString("N");
        var executable = Path.Combine(Paths.VersionsRoot, installationId, "app", "PhotoShelf.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!); File.WriteAllText(executable, "synthetic");
        Directory.CreateDirectory(Paths.ProgramRoot);
        File.WriteAllText(Paths.ActivePointerPath, JsonSerializer.Serialize(
            new ActiveInstallationPointer(1, installationId, "1.11.2"), JsonOptions));
        var operation = Path.Combine(Paths.OperationsRoot, requestId); Directory.CreateDirectory(operation);
        var previousRequest = Environment.GetEnvironmentVariable(UpdateStartupHealth.RequestVariable);
        var previousInstallation = Environment.GetEnvironmentVariable(UpdateStartupHealth.InstallationVariable);
        try
        {
            Environment.SetEnvironmentVariable(UpdateStartupHealth.RequestVariable, requestId);
            Environment.SetEnvironmentVariable(UpdateStartupHealth.InstallationVariable, installationId);
            UpdateStartupHealth.ReportReady(executable, Paths);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateStartupHealth.RequestVariable, previousRequest);
            Environment.SetEnvironmentVariable(UpdateStartupHealth.InstallationVariable, previousInstallation);
        }

        Assert.False(Directory.Exists(stage)); Assert.False(File.Exists(requestPath));
        Assert.True(File.Exists(Path.Combine(operation, "startup-ready.json")));
        Assert.True(File.Exists(executable)); Assert.True(File.Exists(Paths.ActivePointerPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
