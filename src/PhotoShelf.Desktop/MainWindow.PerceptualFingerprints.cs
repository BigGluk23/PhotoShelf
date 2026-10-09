using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using PhotoShelf.Application.Background;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Duplicates;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Desktop;

public partial class MainWindow
{
    internal Func<string, CancellationToken, PerceptualFingerprint> FingerprintReader { get; set; } =
        MediaBitmapLoader.LoadPerceptualFingerprint;
    private FingerprintActivity _fingerprintActivity = new();
    private sealed record FingerprintProgress(string Phase = "ожидание", long Processed = 0,
        long Errors = 0, string? CurrentPath = null, string? Error = null, long Started = 0,
        DateTime? LastProgressUtc = null, long Finished = 0);
    private sealed class FingerprintActivity
    {
        private FingerprintProgress _value = new();
        public FingerprintProgress Value { get => Volatile.Read(ref _value); set => Volatile.Write(ref _value, value); }
    }

    private void StartPerceptualFingerprintIndexing()
    {
        if (_fileOperationActive || _hasPendingRecovery || !_catalogLoaded || _closing || _backgroundProcessingPaused) return;
        if (!_fingerprintTask.IsCompleted)
        {
            _fingerprintRefreshPending = true;
            return;
        }
        // Consume all requests accumulated before this pass, including a request
        // retained while paused. A blocked start must leave that request intact.
        _fingerprintRefreshPending = false;
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _fingerprintCancellation = operation;
        _fingerprintActivity = new FingerprintActivity();
        _fingerprintTask = RunPerceptualFingerprintWorkerAsync(operation, _fingerprintActivity);
    }

    private async Task RunPerceptualFingerprintWorkerAsync(CancellationTokenSource operation, FingerprintActivity activity)
    {
        using var workActivity = BackgroundWorkController.Shared.Begin(BackgroundTaskKind.Fingerprints);
        try
        {
            var token = operation.Token;
            // The derived index covers the catalog, not only today's checked tree nodes. That
            // keeps explicit "whole library" and later folder selections useful without a new
            // decode pass. Search/review scope is still applied when a session is created.
            var scope = new PerceptualFingerprintScope([], [], _includeSystemFolders, DateTime.UtcNow);
            activity.Value = new("индексирую визуальное сходство", Started: Stopwatch.GetTimestamp());
            await Task.Run(async () =>
            {
                var pending = new List<PerceptualFingerprintCatalogUpdate>(32);
                var sinceCommit = Stopwatch.StartNew();
                var workBudget = BackgroundWorkController.Shared.CreatePacer();

                async Task CommitAsync()
                {
                    if (pending.Count == 0) return;
                    workActivity.SetPhase(BackgroundTaskPhase.Saving);
                    activity.Value = activity.Value with { Phase = "сохраняю визуальный индекс" };
                    var batch = pending.ToArray();
                    pending.Clear();
                    var committed = await _perceptualFingerprintStore.SaveObservedBatchAsync(batch, token);
                    var accepted = committed.Count(value => value);
                    var progress = activity.Value;
                    activity.Value = progress with
                    {
                        Phase = "индексирую визуальное сходство",
                        Processed = progress.Processed + accepted,
                        LastProgressUtc = accepted == 0 ? progress.LastProgressUtc : DateTime.UtcNow
                    };
                    sinceCommit.Restart();
                }

                async Task IndexOneAsync(SavedMediaItem expected)
                {
                    workActivity.SetPhase(BackgroundTaskPhase.Reading);
                    activity.Value = activity.Value with { CurrentPath = expected.Path };
                    var before = await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Hash,
                        _ => ToSavedItem(expected.Path), token, workActivity);
                    if (before is null)
                    {
                        pending.Add(new(expected,
                            PerceptualFingerprintReadResult.Transient("unavailable-file", DateTime.UtcNow.AddHours(1)),
                            DateTime.UtcNow));
                        return;
                    }
                    if (!SameFileObservation(before, expected))
                    {
                        var probe = new FileSystemObservationProbe().ProbeFile(expected.Path);
                        if (probe.Availability == FileAvailability.Available)
                            await _desktopCatalogStore.ApplyObservationAsync(new(LibraryCatalogSynchronizer.Available(probe)), expected, token);
                        return;
                    }

                    var attempted = DateTime.UtcNow;
                    PerceptualFingerprintReadResult result;
                    try
                    {
                        var fingerprint = await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Hash,
                            readToken => FingerprintReader(expected.Path, readToken), token, workActivity);
                        result = PerceptualFingerprintReadResult.Found(fingerprint);
                    }
                    catch (NotSupportedException ex) { result = PerceptualFingerprintReadResult.Unsupported(ErrorCode(ex)); }
                    catch (FileFormatException ex) { result = PerceptualFingerprintReadResult.Corrupt(ErrorCode(ex)); }
                    catch (UnauthorizedAccessException ex)
                    {
                        result = PerceptualFingerprintReadResult.Transient(ErrorCode(ex), DateTime.UtcNow.AddHours(6));
                    }
                    catch (IOException ex)
                    {
                        result = PerceptualFingerprintReadResult.Transient(ErrorCode(ex), DateTime.UtcNow.AddHours(1));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result = PerceptualFingerprintReadResult.Transient(ErrorCode(ex), DateTime.UtcNow.AddHours(6));
                    }
                    token.ThrowIfCancellationRequested();
                    var after = await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Hash,
                        _ => ToSavedItem(expected.Path), token, workActivity);
                    if (after is null || !SameFileObservation(before, after)) return;
                    pending.Add(new(expected, result, attempted));
                    if (result.Status != PerceptualFingerprintStatus.Found)
                    {
                        workActivity.Progress(0, 1);
                        var progress = activity.Value;
                        activity.Value = progress with { Errors = progress.Errors + 1, Error = result.ErrorCode };
                    }
                }

                string? afterPath = null;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var page = await _perceptualFingerprintStore.QueryDuePageAsync(scope, afterPath, 64, token);
                    if (page.Count == 0) break;
                    foreach (var item in page)
                    {
                        token.ThrowIfCancellationRequested();
                        await IndexOneAsync(item);
                        workActivity.Progress();
                        if (pending.Count >= 32 || sinceCommit.ElapsedMilliseconds >= 250) await CommitAsync();
                        await workBudget.CheckpointAsync(token, workActivity);
                    }
                    afterPath = page[^1].Path;
                }
                await CommitAsync();
                activity.Value = activity.Value with { Phase = "готово", CurrentPath = null };
            }, token);
            workActivity.Finish(BackgroundTaskPhase.Completed);
        }
        catch (OperationCanceledException)
        {
            workActivity.Finish(BackgroundTaskPhase.Cancelled);
            activity.Value = activity.Value with { Phase = "остановлено", CurrentPath = null };
        }
        catch (Exception ex)
        {
            activity.Value = activity.Value with { Phase = "ошибка", Error = ex.Message, CurrentPath = null };
        }
        finally
        {
            activity.Value = activity.Value with { Finished = Stopwatch.GetTimestamp() };
            if (ReferenceEquals(_fingerprintCancellation, operation))
            {
                _fingerprintCancellation = null;
                if (_fingerprintRefreshPending && !_closing)
                {
                    // This task is still incomplete inside its finally. Restart on
                    // the next dispatcher turn, after readers and this task finish.
                    // Start rechecks the current pause/move/recovery/closing guards;
                    // a newer explicit start may already have consumed the request.
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_fingerprintRefreshPending) StartPerceptualFingerprintIndexing();
                    }), DispatcherPriority.Background);
                }
            }
            operation.Dispose();
        }
    }

    private static bool SameFileObservation(SavedMediaItem left, SavedMediaItem right) =>
        left.SizeBytes == right.SizeBytes &&
        left.FileModifiedAt?.ToUniversalTime().Ticks == right.FileModifiedAt?.ToUniversalTime().Ticks;

    private static string ErrorCode(Exception exception) => exception.GetType().Name;
}
