using System.Diagnostics;
using System.IO;
using PhotoShelf.Application.Background;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Duplicates;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Desktop;

public partial class MainWindow
{
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
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _fingerprintCancellation = operation;
        _fingerprintActivity = new FingerprintActivity();
        _fingerprintTask = RunPerceptualFingerprintWorkerAsync(operation, _fingerprintActivity);
    }

    private async Task RunPerceptualFingerprintWorkerAsync(CancellationTokenSource operation, FingerprintActivity activity)
    {
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
                var workBudget = Stopwatch.StartNew();

                async Task CommitAsync()
                {
                    if (pending.Count == 0) return;
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
                    activity.Value = activity.Value with { CurrentPath = expected.Path };
                    var before = await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Hash,
                        _ => ToSavedItem(expected.Path), token);
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
                            readToken => MediaBitmapLoader.LoadPerceptualFingerprint(expected.Path, readToken), token);
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
                        _ => ToSavedItem(expected.Path), token);
                    if (after is null || !SameFileObservation(before, after)) return;
                    pending.Add(new(expected, result, attempted));
                    if (result.Status != PerceptualFingerprintStatus.Found)
                    {
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
                        if (pending.Count >= 32 || sinceCommit.ElapsedMilliseconds >= 250) await CommitAsync();
                        // Fingerprinting is deliberately background-paced. The shared scheduler
                        // also reserves an execution slot for visible previews and interaction.
                        if (workBudget.ElapsedMilliseconds >= 75)
                        {
                            await Task.Delay((int)Math.Min(100, workBudget.ElapsedMilliseconds), token);
                            workBudget.Restart();
                        }
                    }
                    afterPath = page[^1].Path;
                }
                await CommitAsync();
                activity.Value = activity.Value with { Phase = "готово", CurrentPath = null };
            }, token);
        }
        catch (OperationCanceledException)
        {
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
                if (_fingerprintRefreshPending && !_closing && !_fileOperationActive && !_hasPendingRecovery && !_backgroundProcessingPaused)
                {
                    _fingerprintRefreshPending = false;
                    StartPerceptualFingerprintIndexing();
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
