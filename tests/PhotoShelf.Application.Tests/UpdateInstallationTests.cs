using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotoShelf.Application.Updates;
using PhotoShelf.Application.Updates.Installation;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class UpdateInstallationTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-update-install-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _key = RSA.Create(2048);
    private UpdateInstallationPaths Paths => new(Path.Combine(_root, "app-data"));
    private string PublicKey => _key.ExportSubjectPublicKeyInfoPem();

    [Fact]
    public async Task SuccessfulInstallKeepsOldProgramCatalogOriginalsAndStageAndReportsRealStartupSeparately()
    {
        var old = await SeedActiveAsync("0.10.14");
        var evidence = await SeedUserEvidenceAsync();
        var stage = await CreateStageAsync("0.10.15");
        var zipBefore = await File.ReadAllBytesAsync(stage.PackagePath);
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14-ultra");
        var host = new FakeProcesses(Paths, reportReady: true);
        var result = await Installer(host).InstallAsync(request);

        Assert.True(result.StartupReady);
        Assert.Equal(new[] { "parent-exited", "isolated-check", "launch" }, host.Events);
        Assert.Equal(result.Target, ActiveInstallationResolver.Resolve(Paths, PublicKey));
        Assert.Equal("0.10.15", result.Target.Version);
        Assert.NotEqual(old.ExecutablePath, result.Target.ExecutablePath);
        Assert.True(File.Exists(old.ExecutablePath));
        Assert.Equal(zipBefore, await File.ReadAllBytesAsync(stage.PackagePath));
        await AssertEvidenceUnchangedAsync(evidence);
        var operation = Directory.GetDirectories(Paths.OperationsRoot).Single();
        Assert.True(File.Exists(Path.Combine(operation, "previous-active.json")));
        Assert.True(File.Exists(Path.Combine(operation, "startup-ready.json")));
    }

    [Fact]
    public async Task ParentStillClosingPreventsAnyCopyVerificationOrActivation()
    {
        var old = await SeedActiveAsync("0.10.14");
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        var host = new FakeProcesses(Paths) { ParentFailure = new TimeoutException("still closing") };
        await Assert.ThrowsAsync<TimeoutException>(() => Installer(host).InstallAsync(request));
        Assert.Equal(old, ActiveInstallationResolver.Resolve(Paths, PublicKey));
        Assert.Single(Directory.GetDirectories(Paths.VersionsRoot));
        Assert.Equal(new[] { "parent-exited" }, host.Events);
    }

    [Fact]
    public async Task ParentIdentityReuseIsRejectedAndARealLivingParentIsNeverKilled()
    {
        using var current = Process.GetCurrentProcess();
        var host = new UpdateProcessHost();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.WaitForParentExitAsync(current.Id,
            current.StartTime.ToUniversalTime().Ticks - TimeSpan.TicksPerSecond, TimeSpan.FromMilliseconds(10), default));
        await Assert.ThrowsAsync<TimeoutException>(() => host.WaitForParentExitAsync(current.Id,
            current.StartTime.ToUniversalTime().Ticks, TimeSpan.FromMilliseconds(10), default));
        Assert.False(current.HasExited);
    }

    [Fact]
    public async Task ModifiedStagedExecutableCannotBeInstalledEvenAfterEarlierDownloadVerification()
    {
        var old = await SeedActiveAsync("0.10.14");
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        await File.WriteAllTextAsync(Path.Combine(stage.PackageDirectory, "PhotoShelf.exe"), "tampered executable");
        var host = new FakeProcesses(Paths);
        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(host).InstallAsync(request));
        Assert.Equal(old, ActiveInstallationResolver.Resolve(Paths, PublicKey));
        Assert.Equal(new[] { "parent-exited" }, host.Events);
    }

    [Fact]
    public async Task AnotherValidSignedReleaseDoesNotReuseConsentForPreviousRelease()
    {
        var old = await SeedActiveAsync("0.10.14");
        var stage = await CreateStageAsync("0.10.15");
        var substituted = await CreateStageAsync("0.10.16");
        var requestPath = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        var request = JsonSerializer.Deserialize<UpdateInstallRequest>(await File.ReadAllTextAsync(requestPath), JsonOptions)!;
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request with { StageDirectory = substituted.StageDirectory }, JsonOptions));
        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(new FakeProcesses(Paths)).InstallAsync(requestPath));
        Assert.Equal(old, ActiveInstallationResolver.Resolve(Paths, PublicKey));
    }

    [Theory]
    [InlineData("parent-exited")]
    [InlineData("stage-verified")]
    [InlineData("program-verified")]
    [InlineData("startup-verified")]
    [InlineData("before-pointer-publish")]
    public async Task InterruptedInstallationBeforeActivationKeepsPriorPointerAndEveryUserByte(string checkpoint)
    {
        var old = await SeedActiveAsync("0.10.14");
        var evidence = await SeedUserEvidenceAsync();
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        var host = new FakeProcesses(Paths);
        var options = new UpdateInstallationOptions { Checkpoint = step => { if (step == checkpoint) throw new InjectedFailure(); } };
        await Assert.ThrowsAsync<InjectedFailure>(() => Installer(host, options).InstallAsync(request));
        Assert.Equal(old, ActiveInstallationResolver.Resolve(Paths, PublicKey));
        Assert.DoesNotContain("launch", host.Events);
        await AssertEvidenceUnchangedAsync(evidence);
    }

    [Theory]
    [InlineData("startup-verified")]
    [InlineData("before-pointer-publish")]
    public async Task CancellationBeforePointerPublicationKeepsOldProgramAndAllUserBytes(string checkpoint)
    {
        var old = await SeedActiveAsync("0.10.14");
        var evidence = await SeedUserEvidenceAsync();
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        using var cancellation = new CancellationTokenSource();
        var host = new FakeProcesses(Paths);
        var options = new UpdateInstallationOptions
        {
            Checkpoint = step => { if (step == checkpoint) cancellation.Cancel(); },
            StartupHealthTimeout = TimeSpan.Zero
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Installer(host, options).InstallAsync(request, cancellation.Token));
        Assert.Equal(old, ActiveInstallationResolver.Resolve(Paths, PublicKey));
        Assert.DoesNotContain("launch", host.Events);
        await AssertEvidenceUnchangedAsync(evidence);
    }

    [Fact]
    public async Task CancellationAfterPointerPublicationDoesNotInterruptLaunchOrRestoreOldCatalog()
    {
        var old = await SeedActiveAsync("0.10.14");
        var evidence = await SeedUserEvidenceAsync();
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        using var cancellation = new CancellationTokenSource();
        var host = new FakeProcesses(Paths, reportReady: true);
        var options = new UpdateInstallationOptions
        {
            Checkpoint = step => { if (step == "pointer-published") cancellation.Cancel(); },
            StartupHealthTimeout = TimeSpan.Zero
        };
        var result = await Installer(host, options).InstallAsync(request, cancellation.Token);
        Assert.True(result.StartupReady);
        Assert.Equal("0.10.15", ActiveInstallationResolver.Resolve(Paths, PublicKey)!.Version);
        Assert.Contains("launch", host.Events);
        Assert.True(File.Exists(old.ExecutablePath));
        await AssertEvidenceUnchangedAsync(evidence);
    }

    [Fact]
    public async Task TransientIncompleteStartupReceiptDoesNotTurnSuccessfulLaunchIntoAnInstallationFailure()
    {
        await SeedActiveAsync("0.10.14");
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        Task? receiptWriter = null;
        var host = new FakeProcesses(Paths)
        {
            AfterLaunch = launch =>
            {
                var path = Path.Combine(Paths.OperationsRoot, launch.RequestId!, "startup-ready.json");
                // Simulate a producer whose final path is visible before its handle is closed.
                // On Windows the reader sees a sharing violation; on other platforms it can see an empty file.
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                receiptWriter = Task.Run(async () =>
                {
                    using (stream)
                    {
                        await Task.Delay(150);
                        var record = new UpdateStartupHealthRecord(1, launch.RequestId!, launch.InstallationId!,
                            "ready", DateTimeOffset.UtcNow);
                        await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions));
                        stream.Flush(flushToDisk: true);
                    }
                });
            }
        };
        try
        {
            var result = await Installer(host, new() { StartupHealthTimeout = TimeSpan.FromSeconds(3) }).InstallAsync(request);
            Assert.True(result.StartupReady);
            Assert.Equal("0.10.15", ActiveInstallationResolver.Resolve(Paths, PublicKey)!.Version);
        }
        finally { if (receiptWriter is not null) await receiptWriter; }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"protocolVersion\":1,\"protocolVersion\":1}")]
    [InlineData("{\"unexpected\":true}")]
    public async Task InvalidStartupReceiptIsUnconfirmedWithoutReversingActivatedInstallation(string content)
    {
        var old = await SeedActiveAsync("0.10.14");
        var evidence = await SeedUserEvidenceAsync();
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        var host = new FakeProcesses(Paths)
        {
            AfterLaunch = launch => File.WriteAllText(
                Path.Combine(Paths.OperationsRoot, launch.RequestId!, "startup-ready.json"), content)
        };
        var result = await Installer(host, new() { StartupHealthTimeout = TimeSpan.Zero }).InstallAsync(request);
        Assert.False(result.StartupReady);
        Assert.Equal("0.10.15", ActiveInstallationResolver.Resolve(Paths, PublicKey)!.Version);
        Assert.Contains("launch", host.Events);
        Assert.True(File.Exists(old.ExecutablePath));
        await AssertEvidenceUnchangedAsync(evidence);
    }

    [Fact]
    public async Task FailedIsolatedCheckNeverActivatesCandidate()
    {
        var old = await SeedActiveAsync("0.10.14");
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        var host = new FakeProcesses(Paths) { CheckFailure = new InvalidDataException("bad resource") };
        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(host).InstallAsync(request));
        Assert.Equal(old, ActiveInstallationResolver.Resolve(Paths, PublicKey));
        Assert.DoesNotContain("launch", host.Events);
        Assert.Equal(2, Directory.GetDirectories(Paths.VersionsRoot).Length); // Candidate retained for diagnosis.
    }

    [Fact]
    public async Task ExecutableModifiedByIsolatedCheckIsCaughtBeforeActivation()
    {
        var old = await SeedActiveAsync("0.10.14");
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        var host = new FakeProcesses(Paths) { AfterCheck = executable => File.WriteAllText(executable, "changed during startup check") };
        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(host).InstallAsync(request));
        Assert.Equal(old, ActiveInstallationResolver.Resolve(Paths, PublicKey));
        Assert.DoesNotContain("launch", host.Events);
    }

    [Fact]
    public async Task FailureAfterPointerPublicationNeverRevertsToPotentiallyIncompatibleOldWriter()
    {
        var old = await SeedActiveAsync("0.10.14");
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        var host = new FakeProcesses(Paths);
        var options = new UpdateInstallationOptions { Checkpoint = step => { if (step == "pointer-published") throw new InjectedFailure(); } };
        await Assert.ThrowsAsync<InjectedFailure>(() => Installer(host, options).InstallAsync(request));
        Assert.Equal("0.10.15", ActiveInstallationResolver.Resolve(Paths, PublicKey)!.Version);
        Assert.True(File.Exists(old.ExecutablePath));
        Assert.DoesNotContain("launch", host.Events);
    }

    [Fact]
    public async Task LaunchFailureOrSlowNormalStartupPreservesActivatedVersionWithoutRollingBackCatalog()
    {
        var old = await SeedActiveAsync("0.10.14");
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        var host = new FakeProcesses(Paths) { LaunchFailure = new IOException("launch failed") };
        await Assert.ThrowsAsync<IOException>(() => Installer(host).InstallAsync(request));
        Assert.Equal("0.10.15", ActiveInstallationResolver.Resolve(Paths, PublicKey)!.Version);
        Assert.True(File.Exists(old.ExecutablePath));
        var nextStage = await CreateStageAsync("0.10.16");
        var nextRequest = await UpdateInstallRequest.CreateFileAsync(nextStage, Paths, "0.10.15");
        var nextHost = new FakeProcesses(Paths);
        var next = new UpdateInstaller(Paths, PublicKey, "0.10.15", nextHost,
            new UpdateInstallationOptions { StartupHealthTimeout = TimeSpan.Zero });
        var result = await InstallWithHandoffAsync(next, nextHost, nextRequest);
        Assert.False(result.StartupReady);
        Assert.Equal("0.10.16", ActiveInstallationResolver.Resolve(Paths, PublicKey)!.Version);
    }

    [Fact]
    public async Task UsedRequestIsNeverReplayedAndStaleConsentRequiresANewUserDecision()
    {
        await SeedActiveAsync("0.10.14");
        var stage = await CreateStageAsync("0.10.15");
        var requestPath = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        var installer = Installer(new FakeProcesses(Paths, reportReady: true));
        await installer.InstallAsync(requestPath);
        // A replay has no new parent handoff: the helper must reject its durable claim directly.
        await Assert.ThrowsAsync<IOException>(() => new UpdateInstaller(Paths, PublicKey, "0.10.14",
            new FakeProcesses(Paths)).InstallAsync(requestPath));
        var request = JsonSerializer.Deserialize<UpdateInstallRequest>(await File.ReadAllTextAsync(requestPath), JsonOptions)!;
        var expired = request with { RequestId = Guid.NewGuid().ToString("N"), ConsentedAtUtc = DateTimeOffset.UtcNow.AddHours(-1) };
        var expiredPath = Path.Combine(Paths.RequestsRoot, expired.RequestId + ".json");
        await File.WriteAllTextAsync(expiredPath, JsonSerializer.Serialize(expired, JsonOptions));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(expiredPath));
        Assert.Equal("0.10.15", ActiveInstallationResolver.Resolve(Paths, PublicKey)!.Version);
    }

    [Fact]
    public async Task OutsideRequestOrStagePathCannotSelectArbitraryFiles()
    {
        var stage = await CreateStageAsync("0.10.15");
        var requestPath = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        var external = Path.Combine(_root, "arbitrary.json");
        File.Copy(requestPath, external);
        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(new FakeProcesses(Paths)).InstallAsync(external));
        var request = JsonSerializer.Deserialize<UpdateInstallRequest>(await File.ReadAllTextAsync(requestPath), JsonOptions)!;
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request with { StageDirectory = _root }, JsonOptions));
        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(new FakeProcesses(Paths)).InstallAsync(requestPath));
        Assert.False(File.Exists(Paths.ActivePointerPath));
    }

    [Fact]
    public async Task InvalidActivePointerFailsClosedRatherThanLaunchingOldDistribution()
    {
        var old = await SeedActiveAsync("0.10.14");
        var pointer = JsonSerializer.Deserialize<ActiveInstallationPointer>(await File.ReadAllTextAsync(Paths.ActivePointerPath), JsonOptions)!;
        await File.WriteAllTextAsync(Paths.ActivePointerPath, JsonSerializer.Serialize(pointer with { InstallationId = "../catalog" }, JsonOptions));
        Assert.Throws<InvalidDataException>(() => ActiveInstallationResolver.Resolve(Paths, PublicKey));
        Assert.True(File.Exists(old.ExecutablePath));
    }

    [Theory]
    [InlineData("--install")]
    [InlineData("--install", "relative.json")]
    [InlineData("--launch", "--install")]
    [InlineData("--resume")]
    public void HelperCliRejectsAmbiguousOrUnconsentedCommands(params string[] arguments) =>
        Assert.Throws<ArgumentException>(() => UpdateHelperArguments.Parse(arguments));

    [Fact]
    public async Task NormalStartupLeaseIsExclusiveAndAnUnownedRetainedLockFileNeverBlocksRecovery()
    {
        var old = await SeedActiveAsync("0.10.14");
        var first = UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.14", old.ExecutablePath);
        Assert.NotNull(first);
        try { Assert.Null(UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.14", old.ExecutablePath)); }
        finally { first.Dispose(); first.Dispose(); }
        Assert.True(File.Exists(Paths.InstallationLockPath));
        using var recovered = UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.14", old.ExecutablePath);
        Assert.NotNull(recovered);
    }

    [Fact]
    public async Task InstallerReservesNormalStartupBeforeParentExitAndKeepsItReservedThroughActivation()
    {
        var old = await SeedActiveAsync("0.10.14");
        var evidence = await SeedUserEvidenceAsync();
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        var checkpoints = new List<string>();
        var host = new FakeProcesses(Paths, reportReady: true);
        var options = new UpdateInstallationOptions
        {
            StartupHealthTimeout = TimeSpan.Zero,
            Checkpoint = step =>
            {
                if (step is not ("startup-reserved" or "handoff-accepted" or "parent-exited" or "before-pointer-publish" or "pointer-published")) return;
                checkpoints.Add(step);
                using var admission = UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.14", old.ExecutablePath);
                Assert.Null(admission);
                if (step == "startup-reserved") Assert.Empty(host.Events);
            }
        };
        var result = await Installer(host, options).InstallAsync(request);
        Assert.True(result.StartupReady);
        Assert.Equal(new[] { "startup-reserved", "handoff-accepted", "parent-exited", "before-pointer-publish", "pointer-published" }, checkpoints);
        using var unlocked = UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.15", result.Target.ExecutablePath);
        Assert.NotNull(unlocked);
        await AssertEvidenceUnchangedAsync(evidence);
    }

    [Fact]
    public async Task MissingParentAcceptanceTimesOutWithoutWaitingForExitOrActivatingAndCannotBeAcceptedLate()
    {
        var old = await SeedActiveAsync("0.10.14");
        var evidence = await SeedUserEvidenceAsync();
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        var host = new FakeProcesses(Paths);
        var installer = new UpdateInstaller(Paths, PublicKey, "0.10.14", host,
            new() { HandoffTimeout = TimeSpan.FromMilliseconds(80) });
        await Assert.ThrowsAsync<TimeoutException>(() => installer.InstallAsync(request));
        Assert.Empty(host.Events);
        Assert.Equal(old, ActiveInstallationResolver.Resolve(Paths, PublicKey));
        using var helper = Process.GetCurrentProcess();
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstallationHandoff.AcceptWhenReadyAsync(request,
            Paths, helper.Id, helper.StartTime.ToUniversalTime().Ticks, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(request));
        Assert.False(File.Exists(Path.Combine(Paths.OperationsRoot, Path.GetFileNameWithoutExtension(request), "parent-accepted.json")));
        using var recovered = UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.14", old.ExecutablePath);
        Assert.NotNull(recovered);
        await AssertEvidenceUnchangedAsync(evidence);
    }

    [Fact]
    public async Task HandoffRejectsWrongHelperIdentityAndCancellationNeverWritesAcceptance()
    {
        var old = await SeedActiveAsync("0.10.14");
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        using var cancellation = new CancellationTokenSource();
        var installer = new UpdateInstaller(Paths, PublicKey, "0.10.14", new FakeProcesses(Paths),
            new() { HandoffTimeout = TimeSpan.FromSeconds(2) });
        var installation = installer.InstallAsync(request, cancellation.Token);
        var ready = Path.Combine(Paths.OperationsRoot, Path.GetFileNameWithoutExtension(request), "helper-ready.json");
        try
        {
            using var helper = Process.GetCurrentProcess();
            await Assert.ThrowsAsync<IOException>(() => UpdateInstallationHandoff.AcceptWhenReadyAsync(request, Paths,
                helper.Id, helper.StartTime.ToUniversalTime().Ticks - 1, TimeSpan.FromSeconds(1)));
            using var parentCancellation = new CancellationTokenSource(); parentCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateInstallationHandoff.AcceptWhenReadyAsync(request,
                Paths, helper.Id, helper.StartTime.ToUniversalTime().Ticks, TimeSpan.FromSeconds(1), parentCancellation.Token));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(ready)!, "parent-accepted.json")));
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installation);
        }
        Assert.Equal(old, ActiveInstallationResolver.Resolve(Paths, PublicKey));
    }

    [Fact]
    public async Task OnlyBoundSignedSuccessorCanStartWhileHelperWaitsForHealthAndStaleContextCannotBypassAnotherLease()
    {
        var old = await SeedActiveAsync("0.10.14");
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        UpdateApplicationLaunch? captured = null;
        var host = new FakeProcesses(Paths, reportReady: true)
        {
            AfterLaunch = launch =>
            {
                captured = launch;
                WithLaunchEnvironment(launch, () =>
                {
                    using (var allowed = UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.15", launch.ExecutablePath))
                        Assert.NotNull(allowed); // No health deadlock although the helper still owns installation.lock.
                    var operation = Path.Combine(Paths.OperationsRoot, launch.RequestId!);
                    var records = new[] { "helper-ready.json", "parent-accepted.json", "helper-consumed.json", "parent-committed.json" }
                        .ToDictionary(name => Path.Combine(operation, name), name => File.ReadAllBytes(Path.Combine(operation, name)));
                    var originalReady = records[Path.Combine(operation, "helper-ready.json")];
                    try
                    {
                        // Even mutually matching ready/ACK cannot authorize another signed package
                        // or a recycled helper identity. Both records remain structurally valid.
                        var altered = System.Text.Json.Nodes.JsonNode.Parse(originalReady)!.AsObject();
                        altered["expectedPackageSha256"] = new string('0', 64);
                        foreach (var path in records.Keys) File.WriteAllText(path, altered.ToJsonString());
                        Assert.Null(UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.15", launch.ExecutablePath));
                        altered = System.Text.Json.Nodes.JsonNode.Parse(originalReady)!.AsObject();
                        altered["helperStartTimeUtcTicks"] = altered["helperStartTimeUtcTicks"]!.GetValue<long>() - 1;
                        foreach (var path in records.Keys) File.WriteAllText(path, altered.ToJsonString());
                        Assert.Null(UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.15", launch.ExecutablePath));
                    }
                    finally
                    {
                        foreach (var (path, bytes) in records) File.WriteAllBytes(path, bytes);
                    }
                    Assert.Null(UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.14", old.ExecutablePath));
                    Assert.Null(UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.15", old.ExecutablePath));
                    Environment.SetEnvironmentVariable(UpdateStartupHealth.InstallationVariable, Guid.NewGuid().ToString("N"));
                    Assert.Null(UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.15", launch.ExecutablePath));
                    Environment.SetEnvironmentVariable(UpdateStartupHealth.InstallationVariable, launch.InstallationId);
                    Environment.SetEnvironmentVariable(UpdateStartupHealth.RequestVariable, Guid.NewGuid().ToString("N"));
                    Assert.Null(UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.15", launch.ExecutablePath));
                });
            }
        };
        var result = await Installer(host).InstallAsync(request);
        Assert.True(result.StartupReady);
        Assert.NotNull(captured);
        using var normalLease = UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.15", result.Target.ExecutablePath);
        Assert.NotNull(normalLease);
        WithLaunchEnvironment(captured, () =>
            Assert.Null(UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.15", result.Target.ExecutablePath)));
    }

    [Fact]
    public async Task AdmittedSuccessorKeepsEntryReservedAfterHelperTimeoutUntilItClaimsTheCatalog()
    {
        await SeedActiveAsync("0.10.14");
        var evidence = await SeedUserEvidenceAsync();
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        UpdateStartupLease? admittedSuccessor = null;
        var host = new FakeProcesses(Paths)
        {
            AfterLaunch = launch => WithLaunchEnvironment(launch, () =>
            {
                admittedSuccessor = UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.15", launch.ExecutablePath);
                Assert.NotNull(admittedSuccessor);
                // The successor is now suspended before acquiring the catalog single-writer mutex.
            })
        };
        try
        {
            var result = await Installer(host).InstallAsync(request);
            Assert.False(result.StartupReady); // Helper health timed out; all its handles are now released.
            using var restarted = UpdateStartupLease.TryAcquire(Paths, PublicKey, "0.10.15", result.Target.ExecutablePath);
            Assert.Null(restarted);
            var nextStage = await CreateStageAsync("0.10.16");
            var blockedRequest = await UpdateInstallRequest.CreateFileAsync(nextStage, Paths, "0.10.15");
            var nextHost = new FakeProcesses(Paths, reportReady: true);
            var nextInstaller = new UpdateInstaller(Paths, PublicKey, "0.10.15", nextHost,
                new() { StartupHealthTimeout = TimeSpan.Zero });
            await Assert.ThrowsAsync<IOException>(() => nextInstaller.InstallAsync(blockedRequest));
            Assert.Empty(nextHost.Events);
            Assert.Equal(result.Target, ActiveInstallationResolver.Resolve(Paths, PublicKey));
            await AssertEvidenceUnchangedAsync(evidence);

            // App releases entry only after it owns its single-writer mutex, or on failed startup.
            admittedSuccessor!.Dispose(); admittedSuccessor = null;
            var retryRequest = await UpdateInstallRequest.CreateFileAsync(nextStage, Paths, "0.10.15");
            var next = await InstallWithHandoffAsync(nextInstaller, nextHost, retryRequest);
            Assert.True(next.StartupReady);
            Assert.Equal("0.10.16", next.Target.Version);
            await AssertEvidenceUnchangedAsync(evidence);
        }
        finally { admittedSuccessor?.Dispose(); }
    }

    [Fact]
    public async Task HelperAlreadyTimedOutCannotMakeParentCommitWhileItsFailureHandlerStillOwnsLocks()
    {
        var old = await SeedActiveAsync("0.10.14");
        var evidence = await SeedUserEvidenceAsync();
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFailure = new ManualResetEventSlim();
        var installer = new UpdateInstaller(Paths, PublicKey, "0.10.14", new FakeProcesses(Paths), new()
        {
            HandoffTimeout = TimeSpan.FromMilliseconds(40),
            Progress = new SynchronousProgress(state =>
            {
                if (state != "installation-stopped") return;
                failed.TrySetResult();
                if (!releaseFailure.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test failure gate timed out.");
            })
        });
        var installation = installer.InstallAsync(request);
        try
        {
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var helper = Process.GetCurrentProcess();
            await Assert.ThrowsAsync<TimeoutException>(() => UpdateInstallationHandoff.AcceptWhenReadyAsync(request, Paths,
                helper.Id, helper.StartTime.ToUniversalTime().Ticks, TimeSpan.FromMilliseconds(150)));
            var operation = Path.Combine(Paths.OperationsRoot, Path.GetFileNameWithoutExtension(request));
            Assert.True(File.Exists(Path.Combine(operation, "parent-accepted.json"))); // Only provisional.
            Assert.False(File.Exists(Path.Combine(operation, "helper-consumed.json")));
            Assert.False(File.Exists(Path.Combine(operation, "parent-committed.json")));
        }
        finally { releaseFailure.Set(); }
        await Assert.ThrowsAsync<TimeoutException>(() => installation);
        Assert.Equal(old, ActiveInstallationResolver.Resolve(Paths, PublicKey));
        await AssertEvidenceUnchangedAsync(evidence);
    }

    [Fact]
    public async Task LaterOrdinaryParentExitWithoutFinalCommitCannotInstallEvenIfHelperConsumedProvisionalAcceptance()
    {
        var old = await SeedActiveAsync("0.10.14");
        var evidence = await SeedUserEvidenceAsync();
        var stage = await CreateStageAsync("0.10.15");
        var request = await UpdateInstallRequest.CreateFileAsync(stage, Paths, "0.10.14");
        var parentExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FakeProcesses(Paths) { BeforeParentExit = () => parentExit.Task };
        using var cancellation = new CancellationTokenSource();
        var installation = new UpdateInstaller(Paths, PublicKey, "0.10.14", host,
            new() { HandoffTimeout = TimeSpan.FromSeconds(3) }).InstallAsync(request, cancellation.Token);
        var operation = Path.Combine(Paths.OperationsRoot, Path.GetFileNameWithoutExtension(request));
        try
        {
            var ready = Path.Combine(operation, "helper-ready.json");
            await WaitForTestFileAsync(ready);
            // Fault input: the parent published its provisional offer but never completed the API.
            // This intentionally omits the final commit; successful tests always use the public API.
            var pending = Path.Combine(operation, "fixture-parent-accepted.pending");
            await File.WriteAllBytesAsync(pending, await File.ReadAllBytesAsync(ready));
            File.Move(pending, Path.Combine(operation, "parent-accepted.json"));
            await WaitForTestFileAsync(Path.Combine(operation, "helper-consumed.json"));
            Assert.False(File.Exists(Path.Combine(operation, "parent-committed.json")));
            parentExit.TrySetResult(); // An unrelated later ordinary close is not installation consent.
            await Assert.ThrowsAsync<InvalidDataException>(() => installation);
            Assert.Equal(new[] { "parent-exited" }, host.Events);
            Assert.Equal(old, ActiveInstallationResolver.Resolve(Paths, PublicKey));
            await AssertEvidenceUnchangedAsync(evidence);
        }
        finally
        {
            cancellation.Cancel(); parentExit.TrySetCanceled();
            try { await installation; } catch (Exception) { }
        }
    }

    private static async Task WaitForTestFileAsync(string path)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("The synthetic fixture did not reach its checkpoint.");
            await Task.Delay(10);
        }
    }

    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    { public void Report(string value) => report(value); }

    private static void WithLaunchEnvironment(UpdateApplicationLaunch launch, Action action)
    {
        var previousRequest = Environment.GetEnvironmentVariable(UpdateStartupHealth.RequestVariable);
        var previousInstallation = Environment.GetEnvironmentVariable(UpdateStartupHealth.InstallationVariable);
        try
        {
            Environment.SetEnvironmentVariable(UpdateStartupHealth.RequestVariable, launch.RequestId);
            Environment.SetEnvironmentVariable(UpdateStartupHealth.InstallationVariable, launch.InstallationId);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateStartupHealth.RequestVariable, previousRequest);
            Environment.SetEnvironmentVariable(UpdateStartupHealth.InstallationVariable, previousInstallation);
        }
    }

    private ConsentingTestParent Installer(FakeProcesses host, UpdateInstallationOptions? options = null) =>
        new(this, new(Paths, PublicKey, "0.10.14-ultra", host, options ?? new() { StartupHealthTimeout = TimeSpan.Zero }), host);

    // The fake process host changes only process effects. Tests perform the same explicit parent
    // acknowledgement as Desktop, rather than disabling the production handoff requirement.
    private async Task<UpdateInstallationResult> InstallWithHandoffAsync(UpdateInstaller installer, FakeProcesses host, string request,
        CancellationToken token = default)
    {
        using var stopAcceptance = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var helper = Process.GetCurrentProcess();
        var parentExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeParentExit = () => parentExit.Task;
        var installation = installer.InstallAsync(request, token);
        var acceptance = UpdateInstallationHandoff.AcceptWhenReadyAsync(request, Paths, helper.Id,
            helper.StartTime.ToUniversalTime().Ticks, TimeSpan.FromSeconds(5), stopAcceptance.Token);
        try
        {
            if (await Task.WhenAny(installation, acceptance) == acceptance)
            {
                await acceptance;
                parentExit.TrySetResult(); // Model real Desktop: actual exit follows final committed handoff.
            }
            return await installation;
        }
        finally
        {
            stopAcceptance.Cancel();
            parentExit.TrySetCanceled();
            try { await acceptance; } catch (Exception) { /* Observe the companion task; preserve the installer failure. */ }
        }
    }

    private sealed class ConsentingTestParent(UpdateInstallationTests fixture, UpdateInstaller installer, FakeProcesses host)
    {
        public Task<UpdateInstallationResult> InstallAsync(string request, CancellationToken token = default) =>
            fixture.InstallWithHandoffAsync(installer, host, request, token);
    }

    private async Task<ActiveInstallationTarget> SeedActiveAsync(string version)
    {
        var stage = await CreateStageAsync(version);
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Paths.VersionsRoot, id);
        var app = Path.Combine(directory, "app");
        Directory.CreateDirectory(app);
        foreach (var source in Directory.EnumerateFiles(stage.PackageDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(app, Path.GetRelativePath(stage.PackageDirectory, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
        }
        File.Copy(stage.ManifestPath, Path.Combine(directory, "photoshelf-update.json"));
        File.Copy(stage.SignaturePath, Path.Combine(directory, "photoshelf-update.sig"));
        await File.WriteAllTextAsync(Paths.ActivePointerPath,
            JsonSerializer.Serialize(new ActiveInstallationPointer(1, id, version), JsonOptions));
        return ActiveInstallationResolver.Resolve(Paths, PublicKey)!;
    }

    private async Task<Dictionary<string, byte[]>> SeedUserEvidenceAsync()
    {
        var files = new Dictionary<string, byte[]>
        {
            [Path.Combine(Paths.AppRoot, "storage-generations", "synthetic", "catalog-v2.sqlite")] = "synthetic catalog bytes"u8.ToArray(),
            [Path.Combine(Paths.AppRoot, "storage-generations", "synthetic", "operations", "move.jsonl")] = "synthetic durable file move journal"u8.ToArray(),
            [Path.Combine(_root, "media", "original.heic")] = "synthetic original photo bytes"u8.ToArray(),
            [Path.Combine(_root, "media", "original.mov")] = "synthetic original video bytes"u8.ToArray()
        };
        foreach (var (path, bytes) in files) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, bytes); }
        return files;
    }

    private static async Task AssertEvidenceUnchangedAsync(Dictionary<string, byte[]> evidence)
    {
        foreach (var (path, expected) in evidence) Assert.Equal(expected, await File.ReadAllBytesAsync(path));
    }

    private async Task<StagedUpdate> CreateStageAsync(string version)
    {
        var root = Path.Combine(Paths.StagingRoot, Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(package);
        var data = new Dictionary<string, byte[]>
        {
            ["PhotoShelf.exe"] = Encoding.UTF8.GetBytes("synthetic app " + version),
            ["PhotoShelf.Updater.exe"] = "synthetic updater"u8.ToArray(),
            ["RUNNING.txt"] = Encoding.UTF8.GetBytes($"PhotoShelf Ultra v{version} — Windows x64\n"),
            ["codecs/heif/PhotoShelf.HeifWorker.exe"] = "synthetic decoder"u8.ToArray(),
            ["codecs/heif/heif.dll"] = "synthetic heif"u8.ToArray(),
            ["codecs/heif/libde265.dll"] = "synthetic codec"u8.ToArray(),
            ["codecs/heif/VERSION.txt"] = "synthetic codec version"u8.ToArray(),
            ["codecs/heif/sources/sources.json"] = "{}"u8.ToArray(),
            ["licenses/NOTICE.txt"] = "synthetic application license"u8.ToArray(),
            ["codecs/heif/licenses/LICENSE.txt"] = "synthetic decoder license"u8.ToArray()
        };
        var inventory = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, product = "PhotoShelf Ultra",
            version = version + "-ultra", commit = new string('a', 40), files = data.Select(pair => new
            { path = pair.Key, length = pair.Value.LongLength, sha256 = Hash(pair.Value) }) }, JsonOptions);
        data.Add("package-manifest.json", inventory);
        foreach (var (relative, bytes) in data)
        {
            var path = Path.Combine(package, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, bytes);
        }
        var zipPath = Path.Combine(root, "package.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            foreach (var (relative, bytes) in data)
            { var entry = zip.CreateEntry(relative); using var output = entry.Open(); output.Write(bytes); }
        var zipBytes = await File.ReadAllBytesAsync(zipPath);
        var manifest = new UpdateManifest { ProtocolVersion = 1, Version = version, Runtime = "win-x64",
            PackageUrl = $"https://github.com/BigGluk23/PhotoShelf/releases/download/v{version}-ultra/PhotoShelf-v{version}-ultra-win-x64.zip",
            PackageSha256 = Hash(zipBytes), PackageManifestSha256 = Hash(inventory), PackageBytes = zipBytes.LongLength,
            UnpackedBytes = data.Sum(pair => pair.Value.LongLength), MinCatalogSchema = 5, MaxCatalogSchema = 5,
            ReleaseNotesUrl = $"https://github.com/BigGluk23/PhotoShelf/releases/tag/v{version}-ultra" };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await File.WriteAllBytesAsync(Path.Combine(root, "photoshelf-update.json"), manifestBytes);
        await File.WriteAllBytesAsync(Path.Combine(root, "photoshelf-update.sig"),
            _key.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return await UpdatePackageVerifier.VerifyStagedAsync(root, PublicKey);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed class InjectedFailure : Exception { }

    private sealed class FakeProcesses(UpdateInstallationPaths paths, bool reportReady = false) : IUpdateProcessHost
    {
        public List<string> Events { get; } = [];
        public Exception? ParentFailure { get; init; }
        public Exception? CheckFailure { get; init; }
        public Exception? LaunchFailure { get; init; }
        public Action<string>? AfterCheck { get; init; }
        public Action<UpdateApplicationLaunch>? AfterLaunch { get; init; }
        public Func<Task>? BeforeParentExit { get; set; }
        public async Task WaitForParentExitAsync(int processId, long startTimeUtcTicks, TimeSpan timeout, CancellationToken token)
        {
            Events.Add("parent-exited");
            if (ParentFailure is not null) throw ParentFailure;
            if (BeforeParentExit is not null) await BeforeParentExit().WaitAsync(token);
        }
        public Task VerifyStartupAsync(string executable, string report, TimeSpan timeout, CancellationToken token)
        {
            Events.Add("isolated-check");
            if (CheckFailure is not null) throw CheckFailure;
            AfterCheck?.Invoke(executable);
            return Task.CompletedTask;
        }
        public void Launch(UpdateApplicationLaunch application)
        {
            Events.Add("launch"); if (LaunchFailure is not null) throw LaunchFailure;
            AfterLaunch?.Invoke(application);
            if (!reportReady) return;
            var path = Path.Combine(paths.OperationsRoot, application.RequestId!, "startup-ready.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new UpdateStartupHealthRecord(1, application.RequestId!,
                application.InstallationId!, "ready", DateTimeOffset.UtcNow), JsonOptions));
        }
    }

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); // This test's exclusively owned synthetic fixture.
    }
}
