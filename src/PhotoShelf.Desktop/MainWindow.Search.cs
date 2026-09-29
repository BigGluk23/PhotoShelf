namespace PhotoShelf.Desktop;

public partial class MainWindow
{
    private bool _searchStopped;
    private bool _searchStopping;
    private Task _searchStopTask = Task.CompletedTask;
    private string? _searchStopError;

    internal Task StopSearchAsync()
    {
        if (_searchStopping)
        {
            // A new request may be queued behind the drain. A later Stop also
            // cancels that request, without waiting on a task which awaits us.
            _scanCancellation?.Cancel();
            _browseCancellation?.Cancel();
            foreach (var root in _pendingFolderScans) RegisterWatchedFolder(root);
            _pendingFolderScans.Clear();
            return _searchStopTask;
        }
        return _searchStopTask = StopSearchCoreAsync();
    }

    private async Task StopSearchCoreAsync()
    {
        // Guard before the first await: queued configuration, a checkbox timer,
        // and publication of committed batches must not restart the traversal.
        _searchStopped = true;
        _searchStopping = true;
        _searchStopError = null;
        _scanCancellation?.Cancel();
        _browseCancellation?.Cancel();
        var scan = _scanTask;
        var browse = _browseTask;
        foreach (var root in _pendingFolderScans) RegisterWatchedFolder(root);
        _pendingFolderScans.Clear();
        RefreshSearchControls();
        try
        {
            await _monitorConfigurationTask;
            if (_libraryMonitor is not null)
            {
                await _libraryMonitor.PauseAsync();
                _monitorPaused = true;
            }
            await Task.WhenAll(scan, browse);
            SaveCatalogState();
        }
        catch (Exception exception)
        {
            // Keep the restart guard after failure. Never announce a successful
            // stop or enable a file operation on the strength of token cancellation.
            _searchStopError = $"Не удалось завершить остановку поиска: {exception.Message}";
            StatusText.Text = _searchStopError;
            // A later explicit request may retry. File operations and shutdown
            // independently drain the coordinator and all workers before proceeding.
        }
        finally
        {
            _searchStopping = false;
            RefreshSearchControls();
        }
    }

    private void RefreshSearchControls(bool scanning = false)
    {
        var activity = _libraryMonitor?.Activity;
        var monitorWork = activity is { IsPaused: false } &&
            (activity.IsProcessing || activity.PendingReconciliationRootCount > 0 || activity.PendingDirectoryCount > 0);
        CancelScanButton.IsEnabled = !_searchStopping && !_searchStopped && !_backgroundProcessingPaused &&
            !_fileOperationActive && !_closing &&
            (scanning || !_scanTask.IsCompleted || !_browseTask.IsCompleted || monitorWork || _pendingFolderScans.Count > 0);
        CancelScanButton.Content = _searchStopping ? "Останавливаю поиск…" : "×  Остановить поиск";
        CancelScanButton.ToolTip = "Остановить поиск и сверку папок. Метаданные управляются отдельно в «Операциях».";
        if (_searchStopping) LibraryStatusText.Text = "Поиск: останавливаю…";
        else if (_searchStopError is not null) LibraryStatusText.Text = _searchStopError;
        else if (_searchStopped) LibraryStatusText.Text = "Поиск остановлен · автообновление: пауза";
    }
}
