using System.Text.Json;
using System.Security.Cryptography;

namespace PhotoShelf.Application.Updates.Installation;

public sealed class UpdateInstallationOptions
{
    public TimeSpan HandoffTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ParentExitTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan StartupVerificationTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan StartupHealthTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public IProgress<string>? Progress { get; init; }
    /// <summary>Fault injection for synthetic tests; the shipped helper supplies no callback.</summary>
    public Action<string>? Checkpoint { get; init; }
}

public sealed record UpdateInstallationResult(ActiveInstallationTarget Target, bool StartupReady);

/// <summary>
/// Installs only explicitly requested signed bytes into a fresh program directory. Catalogs, media,
/// previous programs and incomplete attempts are never removed, overwritten or restored by this service.
/// </summary>
public sealed class UpdateInstaller(UpdateInstallationPaths paths, string publicKeyPem, string currentVersion,
    IUpdateProcessHost? processHost = null, UpdateInstallationOptions? options = null)
{
    private readonly IUpdateProcessHost _processes = processHost ?? new UpdateProcessHost();
    private readonly UpdateInstallationOptions _options = options ?? new();

    public Task<UpdateInstallationResult> InstallAsync(string requestPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => InstallCoreAsync(requestPath, cancellationToken), cancellationToken);

    private async Task<UpdateInstallationResult> InstallCoreAsync(string requestPath, CancellationToken token)
    {
        var request = UpdateInstallRequest.Read(requestPath, paths, DateTimeOffset.UtcNow);
        if (!UpdateVersion.TryParse(currentVersion, out var helperVersion) ||
            !UpdateVersion.TryParse(request.CurrentVersion, out var requesterVersion) || helperVersion != requesterVersion)
            throw new InvalidDataException("The installation helper does not match the requesting application version.");
        // Reserve normal startup while the original parent still owns its catalog mutex. Keep the
        // gate until health completes; the claim declared below must be disposed BEFORE this lease.
        using var installationLock = UpdateStartupLease.AcquireForInstallation(paths);
        UpdateInstallationPaths.CreateDirectory(paths.OperationsRoot);
        var operationDirectory = paths.OperationDirectory(request.RequestId);
        UpdateInstallationPaths.CreateDirectory(operationDirectory);
        // A durable, no-overwrite claim makes a request single-use, including after a crash or reboot.
        using var claim = new FileStream(Path.Combine(operationDirectory, "request-claimed.json"), FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(claim, request, UpdateManifestVerifier.JsonOptions);
        claim.Flush(flushToDisk: true);
        var journal = new InstallationJournal(operationDirectory, _options.Progress);
        string? installationId = null;
        var activated = false;
        try
        {
            _options.Checkpoint?.Invoke("startup-reserved");
            var handoff = await UpdateInstallationHandoff.ReserveAndWaitForAcceptanceAsync(request, paths,
                _options.HandoffTimeout, token).ConfigureAwait(false);
            _options.Checkpoint?.Invoke("handoff-accepted");
            journal.Write("waiting-for-parent");
            await _processes.WaitForParentExitAsync(request.ParentProcessId, request.ParentStartTimeUtcTicks,
                _options.ParentExitTimeout, token).ConfigureAwait(false);
            UpdateInstallationHandoff.RequireCommitted(paths, handoff);
            _options.Checkpoint?.Invoke("parent-exited");
            token.ThrowIfCancellationRequested();
            var current = ActiveInstallationResolver.Resolve(paths, publicKeyPem, UpdateTrust.CurrentCatalogSchema);
            if (!UpdateVersion.IsNewer(request.ExpectedVersion, currentVersion) ||
                current is not null && !UpdateVersion.IsNewer(request.ExpectedVersion, current.Version))
                throw new InvalidDataException("An equal or newer application is already installed. No downgrade was activated.");

            journal.Write("verifying-stage");
            var staged = await UpdatePackageVerifier.VerifyStagedAsync(request.StageDirectory, publicKeyPem,
                UpdateTrust.CurrentCatalogSchema, token).ConfigureAwait(false);
            if (staged.Release.Version != request.ExpectedVersion ||
                staged.Release.Manifest.PackageSha256 != request.ExpectedPackageSha256)
                throw new InvalidDataException("The prepared release changed after explicit installation consent.");
            _options.Checkpoint?.Invoke("stage-verified");
            EnsureAvailableSpace(staged.Release.Manifest.UnpackedBytes);
            UpdateInstallationPaths.CreateDirectory(paths.VersionsRoot);
            installationId = Guid.NewGuid().ToString("N");
            var installationDirectory = paths.InstallationDirectory(installationId);
            UpdateInstallationPaths.CreateDirectory(installationDirectory);
            var appDirectory = Path.Combine(installationDirectory, "app");
            UpdateInstallationPaths.CreateDirectory(appDirectory);
            journal.Write("copying-program", installationId);
            await CopyProgramAsync(staged, appDirectory, token).ConfigureAwait(false);
            UpdateInstallationPaths.WriteNewDurably(Path.Combine(installationDirectory, "photoshelf-update.json"),
                staged.Release.ManifestBytes.Span);
            UpdateInstallationPaths.WriteNewDurably(Path.Combine(installationDirectory, "photoshelf-update.sig"),
                staged.Release.SignatureBytes.Span);
            await UpdatePackageVerifier.VerifyExtractedDirectoryAsync(appDirectory, staged.Release, token).ConfigureAwait(false);
            _options.Checkpoint?.Invoke("program-verified");
            journal.Write("checking-isolated-startup", installationId);
            var executable = Path.Combine(appDirectory, "PhotoShelf.exe");
            await _processes.VerifyStartupAsync(executable, Path.Combine(operationDirectory, "isolated-startup.json"),
                _options.StartupVerificationTimeout, token).ConfigureAwait(false);
            // The check must not have changed the authenticated package before activation.
            await UpdatePackageVerifier.VerifyExtractedDirectoryAsync(appDirectory, staged.Release, token).ConfigureAwait(false);
            _options.Checkpoint?.Invoke("startup-verified");
            token.ThrowIfCancellationRequested();
            var pointer = new ActiveInstallationPointer(1, installationId, staged.Release.Version);
            journal.Write("ready-to-activate", installationId);
            PublishPointer(pointer, operationDirectory, token);
            activated = true;
            _options.Checkpoint?.Invoke("pointer-published");
            journal.Write("activated", installationId);
            // No rollback is permitted from this point: a launched application may already write its catalog.
            _processes.Launch(new(executable, request.RequestId, installationId));
            journal.Write("application-started", installationId);
            // Cancellation can abandon preparation, but must never interrupt or reverse an activated launch.
            var ready = await ObserveStartupHealthAsync(operationDirectory, request.RequestId, installationId, CancellationToken.None).ConfigureAwait(false);
            journal.Write(ready ? "startup-ready" : "startup-not-yet-confirmed", installationId);
            return new(new(executable, installationId, staged.Release.Version), ready);
        }
        catch (Exception error)
        {
            // Best effort diagnostics cannot replace the original failure. Unknown files/attempts remain untouched.
            try { journal.Write(activated ? "activation-needs-review" : "installation-stopped", installationId, error.GetType().Name); }
            catch (Exception diagnosticError) when (diagnosticError is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private void EnsureAvailableSpace(long unpackedBytes)
    {
        var volume = new DriveInfo(Path.GetPathRoot(paths.ProgramRoot)!);
        var required = checked(unpackedBytes + Math.Max(64L * 1024 * 1024, unpackedBytes / 10));
        if (volume.AvailableFreeSpace < required)
            throw new IOException("There is not enough free space for a separate verified application copy. The previous installation was retained.");
    }

    private static async Task CopyProgramAsync(StagedUpdate staged, string destination, CancellationToken token)
    {
        var inventory = UpdatePackageVerifier.ReadBoundedFile(Path.Combine(staged.PackageDirectory, "package-manifest.json"), 256 * 1024);
        if (Convert.ToHexStringLower(SHA256.HashData(inventory)) != staged.Release.Manifest.PackageManifestSha256)
            throw new InvalidDataException("The source inventory changed before installation.");
        foreach (var file in staged.Files)
        {
            token.ThrowIfCancellationRequested();
            var normalized = file.Path.Replace('/', Path.DirectorySeparatorChar);
            var sourcePath = Path.GetFullPath(Path.Combine(staged.PackageDirectory, normalized));
            var outputPath = Path.GetFullPath(Path.Combine(destination, normalized));
            UpdateInstallationPaths.RequireDescendant(sourcePath, staged.PackageDirectory);
            UpdateInstallationPaths.RequireDescendant(outputPath, destination);
            UpdateInstallationPaths.RejectLinks(sourcePath);
            UpdateInstallationPaths.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length != file.Length) throw new InvalidDataException("The staged file changed before installation.");
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await UpdatePackageVerifier.CopyBoundedAsync(input, output, file.Length, token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        token.ThrowIfCancellationRequested();
        UpdateInstallationPaths.WriteNewDurably(Path.Combine(destination, "package-manifest.json"), inventory);
    }

    private void PublishPointer(ActiveInstallationPointer pointer, string operationDirectory, CancellationToken token)
    {
        UpdateInstallationPaths.RejectLinks(paths.ActivePointerPath, allowMissing: true);
        byte[]? previous = null;
        try { previous = UpdateInstallationPaths.ReadBounded(paths.ActivePointerPath, 64 * 1024); }
        catch (FileNotFoundException) { }
        if (previous is not null)
            UpdateInstallationPaths.WriteNewDurably(Path.Combine(operationDirectory, "previous-active.json"), previous);
        var pending = Path.Combine(paths.ProgramRoot, "active-v1.pending-" + pointer.InstallationId + ".json");
        UpdateInstallationPaths.WriteNewDurably(pending, JsonSerializer.SerializeToUtf8Bytes(pointer, UpdateManifestVerifier.JsonOptions));
        _options.Checkpoint?.Invoke("before-pointer-publish");
        // This is the final cancellable boundary. Preparing durable metadata must not consume a
        // cancellation received after the earlier startup check. Passing this boundary starts the
        // non-cancellable activation stage: publication and launch are never reversed afterwards.
        token.ThrowIfCancellationRequested();
        if (previous is null) File.Move(pending, paths.ActivePointerPath, overwrite: false);
        else File.Replace(pending, paths.ActivePointerPath, destinationBackupFileName: null);
        // Both possible pointer values name retained validated programs. Never touch catalog files here.
    }

    private async Task<bool> ObserveStartupHealthAsync(string operationDirectory, string requestId, string installationId,
        CancellationToken token)
    {
        var until = DateTimeOffset.UtcNow + _options.StartupHealthTimeout;
        var healthPath = Path.Combine(operationDirectory, "startup-ready.json");
        do
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var health = UpdateInstallationPaths.ReadJson<UpdateStartupHealthRecord>(healthPath);
                return health.ProtocolVersion == 1 && health.RequestId == requestId &&
                    health.InstallationId == installationId && health.State == "ready";
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                // A busy, incomplete or unreadable receipt is not evidence that the application failed.
                // Retry only within the existing health deadline; never roll back its catalog or program.
            }
            if (DateTimeOffset.UtcNow >= until) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        } while (true);
    }

    private sealed class InstallationJournal(string directory, IProgress<string>? progress)
    {
        private int _sequence;
        public void Write(string state, string? installationId = null, string? error = null)
        {
            var path = Path.Combine(directory, $"{++_sequence:D4}-{state}.json");
            UpdateInstallationPaths.WriteNewDurably(path, JsonSerializer.SerializeToUtf8Bytes(new
            { protocolVersion = 1, state, installationId, error, atUtc = DateTimeOffset.UtcNow }, UpdateManifestVerifier.JsonOptions));
            progress?.Report(state);
        }
    }
}
