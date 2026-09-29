using System.Diagnostics;
using System.Windows.Threading;

namespace PhotoShelf.Desktop;

public sealed record BackgroundActivitySnapshot(string Summary, string Details, bool Paused, bool CanToggle);

public partial class MainWindow
{
    private bool _backgroundProcessingPaused;
    private bool _backgroundPauseChanging;
    private MetadataActivity _metadataActivity = new();
    private sealed record MetadataProgress(string Phase = "Ожидание", long Due = 0, long Processed = 0,
        long Deferred = 0, long Errors = 0, string? CurrentPath = null, long Started = 0,
        DateTime? LastProgressUtc = null, string? Error = null, long Finished = 0);
    // Each worker owns one instance. A cancelled predecessor can publish committed rows,
    // but cannot replace the activity of its successor.
    private sealed class MetadataActivity
    {
        private MetadataProgress _value = new();
        public MetadataProgress Value { get => Volatile.Read(ref _value); set => Volatile.Write(ref _value, value); }
    }

    private void StartBackgroundActivityDisplay()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            if (_closing) return;
            var progress = _metadataActivity.Value;
            MetadataStatusText.Text = _backgroundPauseChanging ? "Фон: останавливаю…" : _backgroundProcessingPaused
                ? "Фон: пауза — «Операции»" : !_metadataTask.IsCompleted
                    ? $"Метаданные: {progress.Processed:N0}; осталось ≈{progress.Due:N0}"
                    : $"Метаданные: {progress.Phase.ToLowerInvariant()}";
            MetadataStatusText.ToolTip = GetBackgroundActivity().Details;
            if (_backgroundProcessingPaused) LibraryStatusText.Text = "Автообновление: пауза";
            else if (_libraryMonitor?.Activity is { IsProcessing: true } activity)
                LibraryStatusText.Text = activity.ActiveReconciliationRoots.Count > 0 ? "Сверяю папки — «Операции»" : "Обрабатываю изменения — «Операции»";
            else if (LibraryStatusText.Text.StartsWith("Сверяю папки", StringComparison.Ordinal) ||
                LibraryStatusText.Text.StartsWith("Обрабатываю изменения", StringComparison.Ordinal))
            {
                var unavailable = _libraryRootStates.Values.Count(x => x.Availability != PhotoShelf.Application.Catalog.FileAvailability.Available);
                LibraryStatusText.Text = unavailable == 0 ? "Автообновление включено" : $"Автообновление: {unavailable} папок требуют внимания";
            }
        };
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    internal BackgroundActivitySnapshot GetBackgroundActivity()
    {
        var progress = _metadataActivity.Value;
        var activity = _libraryMonitor?.Activity;
        var elapsed = progress.Started == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(progress.Started,
            progress.Finished == 0 ? Stopwatch.GetTimestamp() : progress.Finished);
        var age = progress.LastProgressUtc is { } last ? Math.Max(0, (DateTime.UtcNow - last).TotalSeconds) : 0;
        var summary = _backgroundPauseChanging ? "Останавливаю фоновые задачи; ожидаю завершения текущего чтения…" :
            _backgroundProcessingPaused ? "Фоновая обработка приостановлена. Просмотр уже добавленных файлов доступен." :
            !_metadataTask.IsCompleted || !_scanTask.IsCompleted || !_browseTask.IsCompleted || activity?.IsProcessing == true
                ? "Выполняются фоновые задачи" : "Фоновые задачи сейчас не выполняются";
        var monitor = activity is null ? "Не запущено" : activity.IsPaused ? "Пауза" : activity.IsProcessing
            ? activity.ActiveReconciliationRoots.Count > 0 ? "Сверка: " + string.Join(", ", activity.ActiveReconciliationRoots) : "Обработка файловых событий"
            : "Ожидание изменений";
        var details = $"Поиск файлов: {(!_scanTask.IsCompleted || !_browseTask.IsCompleted ? "выполняется" : "не выполняется")}\n" +
            $"Метаданные: {progress.Phase}. Обработано в проходе: {progress.Processed:N0}; осталось: ≈{progress.Due:N0}; ждут повтора: {progress.Deferred:N0}; не прочитано: {progress.Errors:N0}.\n" +
            $"Время прохода: {elapsed:hh\\:mm\\:ss}. Последний результат: {(progress.LastProgressUtc is null ? "ещё не получен" : $"{age:N0} с назад")}.\n" +
            (progress.CurrentPath is null ? "" : $"Текущий файл: {progress.CurrentPath}\n") +
            (progress.Error is null ? "" : $"Последняя ошибка: {progress.Error}\n") +
            $"Автообновление: {monitor}. В очереди файлов: {activity?.PendingPathCount ?? 0:N0}, папок: {(activity?.PendingReconciliationRootCount ?? 0) + (activity?.PendingDirectoryCount ?? 0):N0}.\n" +
            "Метаданные дополняют уже добавленные записи: общее число файлов при этом не растёт. Оригиналы не изменяются.";
        return new(summary, details, _backgroundProcessingPaused,
            !_backgroundPauseChanging && !_fileOperationActive && !_closing && _catalogLoaded && !_hasPendingRecovery);
    }

    internal async Task ToggleBackgroundProcessingAsync()
    {
        if (!GetBackgroundActivity().CanToggle) return;
        _backgroundPauseChanging = true;
        try
        {
            if (!_backgroundProcessingPaused)
            {
                // Set the restart guard before cancellation. Watcher notifications remain
                // bounded and queued; no catalog rows, originals, or journals are removed.
                _backgroundProcessingPaused = true;
                _scanCancellation?.Cancel(); _browseCancellation?.Cancel(); _metadataIndexCancellation?.Cancel();
                await _monitorConfigurationTask;
                if (_libraryMonitor is not null) { await _libraryMonitor.PauseAsync(); _monitorPaused = true; }
                await Task.WhenAll(_scanTask, _browseTask, _metadataTask);
            }
            else _backgroundProcessingPaused = false;
            _saveCancellation?.Cancel();
            await _pendingSave;
            await PersistStateAsync(CaptureState(), _lifetime.Token, 0);
            if (!_backgroundProcessingPaused)
            {
                QueueLibraryMonitoring();
                StartMetadataIndexing(false);
                if (_viewMode == LibraryViewMode.Folder && _activeFolder is not null) _ = BrowseFolderAsync(_activeFolder);
            }
        }
        finally { _backgroundPauseChanging = false; }
    }
}
