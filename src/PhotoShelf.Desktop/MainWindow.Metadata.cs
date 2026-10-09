using System.Diagnostics;
using System.Windows.Threading;
using PhotoShelf.Application.Background;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Metadata;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Desktop;

public partial class MainWindow
{
    private void StartMetadataIndexing(bool resetExisting)
    {
        if (_fileOperationActive || _hasPendingRecovery || !_catalogLoaded || _closing || _backgroundProcessingPaused) return;
        StartPerceptualFingerprintIndexing();
        if (!_metadataTask.IsCompleted && !resetExisting)
        {
            // A scan/browse completion adds work; it must not repeatedly cancel a progressing pass.
            _metadataRefreshPending = true;
            return;
        }
        _metadataIndexCancellation?.Cancel();
        var previous = _metadataTask;
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _metadataIndexCancellation = operation;
        _metadataActivity = new MetadataActivity();
        _metadataTask = RunMetadataWorkerAsync(resetExisting, operation, previous, _metadataActivity);
    }

    private async Task RunMetadataWorkerAsync(bool resetExisting, CancellationTokenSource operation, Task previous, MetadataActivity activity)
    {
        var updates = new List<SavedMediaItem>(128);
        var groupingChanged = false;
        async Task PublishBatchAsync()
        {
            if (updates.Count == 0) return;
            var batch = updates.ToArray(); updates.Clear();
            var orderChanged = groupingChanged; groupingChanged = false;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_lifetime.IsCancellationRequested || _closing) return;
                (PhotoRows as VirtualPhotoRows)?.ApplyMetadata(batch);
                foreach (var saved in batch)
                    if (_selection.Find(saved.Path) is { } selected && selected.FileSizeBytes == saved.SizeBytes &&
                        selected.FileModifiedAt == saved.FileModifiedAt && selected.ObservationVersion == saved.ObservationVersion)
                        selected.ApplyIndexedCaptureDate(saved.CaptureDate, saved.MetadataStatus);
                if (orderChanged && (_dateGroupingMode == DateGroupingMode.CaptureDate || _showOnlyMissingCaptureDate) &&
                    IsCurrentViewAffected(new(batch, [], [], false))) NoteCatalogChanged();
            }, DispatcherPriority.Background);
        }
        using var workActivity = BackgroundWorkController.Shared.Begin(BackgroundTaskKind.Metadata);
        try
        {
            activity.Value = new("Ожидание завершения предыдущего чтения");
            workActivity.SetPhase(BackgroundTaskPhase.WaitingForReader);
            await previous;
            workActivity.SetPhase(BackgroundTaskPhase.Preparing);
            var token = operation.Token; token.ThrowIfCancellationRequested();
            var rules = _folderInclusion.Snapshot();
            activity.Value = new("Подготовка очереди", Started: Stopwatch.GetTimestamp());
            await Task.Run(async () =>
            {
                if (resetExisting) await _desktopCatalogStore.ResetMetadataIndexAsync(token);
                var scope = new CatalogViewQuery { IncludeSystemFolders = true, MetadataDueAtUtc = DateTime.UtcNow,
                    ExcludedFolders = rules.ExcludedFolders, IncludedFolders = rules.IncludedFolders };
                var initial = await _desktopCatalogStore.GetMetadataQueueSummaryAsync(scope, token);
                activity.Value = activity.Value with { Due = initial.DueCount, Deferred = initial.DeferredRetryCount };
                var pending = new List<(MetadataCatalogUpdate Update, SavedMediaItem Fresh)>(64);
                var sinceCommit = Stopwatch.StartNew();
                var workBudget = BackgroundWorkController.Shared.CreatePacer();

                async Task CommitAsync()
                {
                    if (pending.Count == 0) return;
                    workActivity.SetPhase(BackgroundTaskPhase.Saving);
                    activity.Value = activity.Value with { Phase = "Запись результатов в каталог" };
                    var batch = pending.ToArray();
                    var committed = await _desktopCatalogStore.UpdateMetadataResultsAsync(batch.Select(x => x.Update).ToArray(), token);
                    // Do not check cancellation between a successful commit and publication.
                    // Results rejected by the fingerprint/version CAS never reach the UI.
                    pending.Clear();
                    for (var i = 0; i < batch.Length; i++)
                    {
                        if (!committed[i]) continue;
                        var (update, fresh) = batch[i];
                        var result = update.Result;
                        var date = result.ApplyTo(update.Expected.CaptureDate);
                        groupingChanged |= update.Expected.CaptureDate != date;
                        fresh.CaptureDate = date; fresh.MetadataIndexed = result.IsTerminal; fresh.MetadataStatus = result.Status;
                        fresh.MetadataAttemptedAtUtc = update.AttemptedAtUtc; fresh.MetadataRetryAtUtc = result.RetryAtUtc(update.AttemptedAtUtc);
                        fresh.MetadataErrorCode = result.ErrorCode;
                        updates.Add(fresh);
                        var progress = activity.Value;
                        activity.Value = progress with { Processed = progress.Processed + 1, Due = Math.Max(0, progress.Due - 1),
                            Deferred = progress.Deferred + (result.Status == MetadataReadStatus.TransientError ? 1 : 0),
                            Errors = progress.Errors + (result.Status is MetadataReadStatus.Found or MetadataReadStatus.Absent ? 0 : 1),
                            LastProgressUtc = DateTime.UtcNow, Error = result.ErrorCode ?? progress.Error };
                    }
                    sinceCommit.Restart();
                    await PublishBatchAsync();
                }

                async Task ReadOneAsync(SavedMediaItem item)
                {
                    workActivity.SetPhase(BackgroundTaskPhase.Reading);
                    activity.Value = activity.Value with { Phase = "Чтение метаданных", CurrentPath = item.Path };
                    var fresh = await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Metadata,
                        _ => ToSavedItem(item.Path), token, workActivity);
                    var result = fresh is null
                        ? new CaptureDateReadResult(MetadataReadStatus.TransientError, ErrorCode: "unavailable-file")
                        : await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Metadata, readToken => CaptureDateReader.Read(item.Path, readToken), token, workActivity);
                    result = result.WithRetryBackoff(item.MetadataAttemptedAtUtc, item.MetadataRetryAtUtc);
                    if (result.Status is not (MetadataReadStatus.Found or MetadataReadStatus.Absent)) workActivity.Progress(0, 1);
                    token.ThrowIfCancellationRequested();
                    var after = fresh is null ? null : await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Metadata,
                        _ => ToSavedItem(item.Path), token, workActivity);
                    if (fresh is not null && (after is null || fresh.SizeBytes != after.SizeBytes || fresh.FileModifiedAt != after.FileModifiedAt)) return;
                    if (fresh is not null && (fresh.SizeBytes != item.SizeBytes || fresh.FileModifiedAt != item.FileModifiedAt))
                    {
                        var probe = new FileSystemObservationProbe().ProbeFile(item.Path);
                        if (probe.Availability == FileAvailability.Available)
                            await _desktopCatalogStore.ApplyObservationAsync(new(LibraryCatalogSynchronizer.Available(probe)), item, token);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (IsCurrentViewAffected(new([item], [], [], false))) NoteCatalogChanged();
                        }, DispatcherPriority.Background);
                        return;
                    }
                    fresh ??= new SavedMediaItem { Path = item.Path, SizeBytes = item.SizeBytes, FileModifiedAt = item.FileModifiedAt,
                        IsVideo = item.IsVideo, IsHiddenOrSystem = item.IsHiddenOrSystem, IsFavorite = item.IsFavorite };
                    fresh.ObservationVersion = item.ObservationVersion; fresh.AssetId = item.AssetId;
                    fresh.Availability = item.Availability; fresh.AvailabilityCheckedAtUtc = item.AvailabilityCheckedAtUtc;
                    fresh.AvailabilityErrorCode = item.AvailabilityErrorCode;
                    pending.Add((new(item, result, DateTime.UtcNow), fresh));
                }

                string? afterPath = null;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var page = await _desktopCatalogStore.QueryMetadataDuePageAsync(scope, afterPath, 128, token);
                    if (page.Count == 0) break;
                    foreach (var item in page)
                    {
                        token.ThrowIfCancellationRequested();
                        await ReadOneAsync(item);
                        workActivity.Progress();
                        if (pending.Count >= 64 || sinceCommit.ElapsedMilliseconds >= 250) await CommitAsync();
                        await workBudget.CheckpointAsync(token, workActivity);
                    }
                    afterPath = page[^1].Path;
                }
                await CommitAsync();
                var remaining = await _desktopCatalogStore.GetMetadataQueueSummaryAsync(scope with { MetadataDueAtUtc = DateTime.UtcNow }, token);
                activity.Value = activity.Value with { Phase = remaining.DueCount > 0 ? "Есть отложенная работа" :
                    remaining.DeferredRetryCount > 0 ? "Ожидание повторных попыток" : "Готово", Due = remaining.DueCount,
                    Deferred = remaining.DeferredRetryCount, CurrentPath = null };
            }, token);
            workActivity.Finish(BackgroundTaskPhase.Completed);
        }
        catch (OperationCanceledException) { workActivity.Finish(BackgroundTaskPhase.Cancelled); activity.Value = activity.Value with { Phase = "Остановлено", CurrentPath = null }; }
        catch (Exception ex) { activity.Value = activity.Value with { Phase = "Ошибка", Error = ex.Message, CurrentPath = null }; }
        finally
        {
            activity.Value = activity.Value with { Finished = Stopwatch.GetTimestamp() };
            await PublishBatchAsync();
            if (ReferenceEquals(_metadataIndexCancellation, operation))
            {
                _metadataIndexCancellation = null;
                CompleteCatalogStage();
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_metadataRefreshPending && !_closing && !_fileOperationActive && !_hasPendingRecovery && !_backgroundProcessingPaused)
                    { _metadataRefreshPending = false; StartMetadataIndexing(false); }
                }), DispatcherPriority.Background);
            }
            operation.Dispose();
        }
    }
}
