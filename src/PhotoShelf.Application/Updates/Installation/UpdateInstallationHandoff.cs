using System.Diagnostics;
using System.Text.Json;

namespace PhotoShelf.Application.Updates.Installation;

internal sealed record UpdateHandoffRecord(int ProtocolVersion, string RequestId, string ReservationId,
    int HelperProcessId, long HelperStartTimeUtcTicks, int ParentProcessId, long ParentStartTimeUtcTicks,
    string ExpectedVersion, string ExpectedPackageSha256)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => ProtocolVersion == 1 && Guid.TryParseExact(RequestId, "N", out _) &&
        Guid.TryParseExact(ReservationId, "N", out _) && HelperProcessId > 0 && HelperStartTimeUtcTicks > 0 &&
        ParentProcessId > 0 && ParentStartTimeUtcTicks > 0 && UpdateVersion.TryParse(ExpectedVersion, out _) &&
        UpdateManifestVerifier.IsSha256(ExpectedPackageSha256);
}

/// <summary>Only the final parent commit authorizes installation after exit. Provisional handoff records never commit a later ordinary close.</summary>
public static class UpdateInstallationHandoff
{
    public static Task AcceptWhenReadyAsync(string requestPath, UpdateInstallationPaths paths, int helperProcessId,
        long helperStartTimeUtcTicks, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var request = UpdateInstallRequest.Read(requestPath, paths, DateTimeOffset.UtcNow);
        using var parent = Process.GetCurrentProcess();
        if (request.ParentProcessId != parent.Id || request.ParentStartTimeUtcTicks != parent.StartTime.ToUniversalTime().Ticks)
            throw new InvalidDataException("The handoff request does not belong to this parent process.");
        var operation = paths.OperationDirectory(request.RequestId);
        var readyPath = Path.Combine(operation, "helper-ready.json");
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!UpdateStartupLease.IsProcessAlive(helperProcessId, helperStartTimeUtcTicks))
                throw new IOException("The installation helper exited before the application handoff was accepted.");
            UpdateHandoffRecord? ready = null;
            try { ready = UpdateInstallationPaths.ReadJson<UpdateHandoffRecord>(readyPath); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (ready is not null)
            {
                if (!ready.IsValid || ready.RequestId != request.RequestId || ready.HelperProcessId != helperProcessId ||
                    ready.HelperStartTimeUtcTicks != helperStartTimeUtcTicks || ready.ParentProcessId != request.ParentProcessId ||
                    ready.ParentStartTimeUtcTicks != request.ParentStartTimeUtcTicks ||
                    ready.ExpectedVersion != request.ExpectedVersion || ready.ExpectedPackageSha256 != request.ExpectedPackageSha256 ||
                    !UpdateStartupLease.IsClaimHeld(Path.Combine(operation, "request-claimed.json")))
                    throw new InvalidDataException("The installation helper handoff does not match the process that was started.");
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.Elapsed >= timeout) throw new TimeoutException("The installation helper did not reserve startup in time.");
                PublishRecord(Path.Combine(operation, "parent-accepted.json"), ready);
                // A ready file and live claim may outlive the helper's decision to time out (for
                // example while reporting failure). Require proof that it consumed this offer.
                await WaitForConsumptionAsync(operation, ready, timer, timeout, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.Elapsed >= timeout || !UpdateStartupLease.IsProcessAlive(helperProcessId, helperStartTimeUtcTicks) ||
                    !UpdateStartupLease.IsClaimHeld(Path.Combine(operation, "request-claimed.json")))
                    throw new TimeoutException("The installation helper stopped before handoff was committed.");
                // No cancellable work follows this durable publication. Desktop must close rather
                // than resume ordinary work after this method succeeds, even if Close itself fails.
                PublishRecord(Path.Combine(operation, "parent-committed.json"), ready);
                return;
            }
            if (timer.Elapsed >= timeout) throw new TimeoutException("The installation helper did not reserve startup in time.");
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }, cancellationToken);

    internal static async Task<UpdateHandoffRecord> ReserveAndWaitForAcceptanceAsync(UpdateInstallRequest request,
        UpdateInstallationPaths paths, TimeSpan timeout, CancellationToken token)
    {
        using var helper = Process.GetCurrentProcess();
        var record = new UpdateHandoffRecord(1, request.RequestId, Guid.NewGuid().ToString("N"), helper.Id,
            helper.StartTime.ToUniversalTime().Ticks, request.ParentProcessId, request.ParentStartTimeUtcTicks,
            request.ExpectedVersion, request.ExpectedPackageSha256);
        var operation = paths.OperationDirectory(request.RequestId);
        PublishRecord(Path.Combine(operation, "helper-ready.json"), record);
        var acceptedPath = Path.Combine(operation, "parent-accepted.json");
        var timer = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (timer.Elapsed >= timeout) throw new TimeoutException("The parent did not accept installation. No update was activated.");
            UpdateHandoffRecord? accepted = null;
            try { accepted = UpdateInstallationPaths.ReadJson<UpdateHandoffRecord>(acceptedPath); }
            catch (FileNotFoundException) { }
            if (accepted is not null)
            {
                if (accepted != record) throw new InvalidDataException("The parent accepted a different installation handoff.");
                PublishRecord(Path.Combine(operation, "helper-consumed.json"), record);
                return record;
            }
            if (!UpdateStartupLease.IsProcessAlive(request.ParentProcessId, request.ParentStartTimeUtcTicks))
                throw new IOException("The parent exited without accepting the installation handoff.");
            await Task.Delay(TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
        }
    }

    internal static void RequireCommitted(UpdateInstallationPaths paths, UpdateHandoffRecord expected)
    {
        UpdateHandoffRecord committed;
        try { committed = UpdateInstallationPaths.ReadJson<UpdateHandoffRecord>(Path.Combine(paths.OperationDirectory(expected.RequestId), "parent-committed.json")); }
        catch (FileNotFoundException) { throw new InvalidDataException("The parent exited without committing installation. No update was activated."); }
        if (committed != expected)
            throw new InvalidDataException("The parent committed a different installation handoff.");
    }

    private static async Task WaitForConsumptionAsync(string operation, UpdateHandoffRecord expected, Stopwatch timer,
        TimeSpan timeout, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (timer.Elapsed >= timeout || !UpdateStartupLease.IsProcessAlive(expected.HelperProcessId, expected.HelperStartTimeUtcTicks) ||
                !UpdateStartupLease.IsClaimHeld(Path.Combine(operation, "request-claimed.json")))
                throw new TimeoutException("The installation helper did not confirm the handoff in time.");
            UpdateHandoffRecord? consumed = null;
            try { consumed = UpdateInstallationPaths.ReadJson<UpdateHandoffRecord>(Path.Combine(operation, "helper-consumed.json")); }
            catch (FileNotFoundException) { }
            if (consumed is not null)
            {
                if (consumed != expected) throw new InvalidDataException("The helper consumed a different installation handoff.");
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
        }
    }

    private static void PublishRecord(string path, UpdateHandoffRecord record)
    {
        var pending = path + ".pending-" + Guid.NewGuid().ToString("N");
        UpdateInstallationPaths.WriteNewDurably(pending, JsonSerializer.SerializeToUtf8Bytes(record, UpdateManifestVerifier.JsonOptions));
        File.Move(pending, path, overwrite: false);
    }
}
