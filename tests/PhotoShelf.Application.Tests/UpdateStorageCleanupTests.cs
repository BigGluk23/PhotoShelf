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
        var expiredView = Path.ChangeExtension(expired, ".view.json");
        var recentView = Path.ChangeExtension(recent, ".view.json");
        var unowned = Path.Combine(Paths.RequestsRoot, "notes.json");
        File.WriteAllText(expired, "{}"); File.WriteAllText(recent, "{}");
        File.WriteAllText(expiredView, "{}"); File.WriteAllText(recentView, "{}"); File.WriteAllText(unowned, "{}");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(expiredView, DateTime.UtcNow.AddHours(-1));

        UpdateStorageCleanup.CleanupAtStartup(Paths.StagingRoot, kept, Paths.RequestsRoot,
            preservedRequestId: null, new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero));

        Assert.True(Directory.Exists(Path.Combine(Paths.StagingRoot, kept)));
        Assert.False(Directory.Exists(Path.Combine(Paths.StagingRoot, orphan)));
        Assert.True(Directory.Exists(Path.Combine(Paths.StagingRoot, unknown)));
        Assert.False(File.Exists(expired)); Assert.False(File.Exists(expiredView));
        Assert.True(File.Exists(recent)); Assert.True(File.Exists(recentView)); Assert.True(File.Exists(unowned));
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
        var viewPath = Path.ChangeExtension(requestPath, ".view.json");
        File.WriteAllText(viewPath, "{\"anchorIndex\":0}");
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

        Assert.False(Directory.Exists(stage)); Assert.False(File.Exists(requestPath)); Assert.False(File.Exists(viewPath));
        Assert.True(File.Exists(Path.Combine(operation, "startup-ready.json")));
        Assert.True(File.Exists(executable)); Assert.True(File.Exists(Paths.ActivePointerPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("{\"protocolVersion\":1,\"requestId\":\"wrong\",\"state\":\"ready\"}")]
    public void UnconfirmedClaimPreservesStageAndExpiredRequestAfterProcessDeath(string? health)
    {
        var (requestId, stage, operation) = CreateClaim();
        if (health is not null) File.WriteAllText(Path.Combine(operation, "startup-ready.json"), health);
        var orphan = Path.Combine(Paths.StagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);

        UpdateStorageCleanup.CleanupAtStartup(Paths.StagingRoot, null, Paths.RequestsRoot,
            operationsRoot: Paths.OperationsRoot);

        Assert.Equal("synthetic package", File.ReadAllText(Path.Combine(stage, "package.zip")));
        Assert.True(File.Exists(Path.Combine(Paths.RequestsRoot, requestId + ".json")));
        Assert.True(File.Exists(Path.Combine(Paths.RequestsRoot, requestId + ".view.json")));
        Assert.True(File.Exists(Path.Combine(operation, "request-claimed.json")));
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void ConfirmedClaimAllowsTransportCleanupButNeverDeletesOperationEvidence()
    {
        var (requestId, stage, operation) = CreateClaim();
        var receipt = new UpdateStartupHealthRecord(1, requestId, Guid.NewGuid().ToString("N"), "ready", DateTimeOffset.UtcNow);
        var healthPath = Path.Combine(operation, "startup-ready.json");
        File.WriteAllText(healthPath, JsonSerializer.Serialize(receipt, JsonOptions));

        UpdateStorageCleanup.CleanupAtStartup(Paths.StagingRoot, null, Paths.RequestsRoot,
            operationsRoot: Paths.OperationsRoot);

        Assert.False(Directory.Exists(stage));
        Assert.False(File.Exists(Path.Combine(Paths.RequestsRoot, requestId + ".json")));
        Assert.False(File.Exists(Path.Combine(Paths.RequestsRoot, requestId + ".view.json")));
        Assert.True(File.Exists(healthPath));
        Assert.True(File.Exists(Path.Combine(operation, "request-claimed.json")));
    }

    [Fact]
    public void UnreadableClaimDoesNotGrantCleanupPermission()
    {
        var (requestId, stage, operation) = CreateClaim();
        File.WriteAllText(Path.Combine(operation, "request-claimed.json"), "{");

        UpdateStorageCleanup.CleanupAtStartup(Paths.StagingRoot, null, Paths.RequestsRoot,
            operationsRoot: Paths.OperationsRoot);

        Assert.True(Directory.Exists(stage));
        Assert.True(File.Exists(Path.Combine(Paths.RequestsRoot, requestId + ".json")));
    }

    [Fact]
    public void SuccessorWithoutPreparedPreferenceNeverPrunesItsTransport()
    {
        var (requestId, stage, _) = CreateClaim();
        UpdateStorageCleanup.CleanupAtStartup(Paths.StagingRoot, null, Paths.RequestsRoot,
            preservedRequestId: requestId, operationsRoot: Paths.OperationsRoot);
        Assert.True(Directory.Exists(stage));
        Assert.True(File.Exists(Path.Combine(Paths.RequestsRoot, requestId + ".json")));
    }

    private (string RequestId, string Stage, string Operation) CreateClaim()
    {
        var requestId = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(Paths.StagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "package.zip"), "synthetic package");
        Directory.CreateDirectory(Paths.RequestsRoot);
        var operation = Path.Combine(Paths.OperationsRoot, requestId);
        Directory.CreateDirectory(operation);
        var request = new UpdateInstallRequest(1, requestId, stage, Environment.ProcessId, 1,
            DateTimeOffset.UtcNow.AddHours(-1), "1.11.1", "1.11.2", new string('a', 64));
        var json = JsonSerializer.Serialize(request, JsonOptions);
        File.WriteAllText(Path.Combine(operation, "request-claimed.json"), json);
        foreach (var suffix in new[] { ".json", ".view.json" })
        {
            var path = Path.Combine(Paths.RequestsRoot, requestId + suffix);
            File.WriteAllText(path, suffix == ".json" ? json : "{}");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        }
        return (requestId, stage, operation);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
