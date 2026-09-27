using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Background;

namespace PhotoShelf.Desktop;

public partial class MainWindow
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _excludedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _projectionCancellation;
    private CancellationTokenSource? _browseCancellation;
    private CancellationTokenSource? _saveCancellation;
    private CancellationTokenSource? _maintenanceCancellation;
    private Task _pendingSave = Task.CompletedTask;
    private Task _metadataTask = Task.CompletedTask;
    private Task _maintenanceTask = Task.CompletedTask;
    private Task _scanTask = Task.CompletedTask;
    private Task _browseTask = Task.CompletedTask;
    private bool _isProjecting;
    private bool _projectionQueued;
    private bool _includeSubfolders = true;
    private bool _closeReady;
    private bool _closing;
    private int _infoRevision;
    private CancellationTokenSource? _infoCancellation;
    private Task _duplicateWorkTask = Task.CompletedTask;
    private readonly HashSet<Task> _infoWorkTasks = new();
    private DuplicateReviewWindow? _activeDuplicateReview;
    private int _treeVisibilityRevision;
    private long _catalogCount;
    private CatalogViewQuery? _currentQuery;
    private string _viewIdentity = "";

    private CatalogViewQuery CreateQuery() => new()
    {
        Folder = _viewMode == LibraryViewMode.Folder ? _activeFolder : null,
        ViewMode = _viewMode.ToString(), IncludeSubfolders = _includeSubfolders,
        ShowVideos = ShowVideos, IncludeSystemFolders = _includeSystemFolders,
        MissingCaptureDateOnly = _showOnlyMissingCaptureDate,
        UseCaptureDate = _dateGroupingMode == DateGroupingMode.CaptureDate,
        NewestFirst = _sortNewestFirst, SearchText = _searchText,
        ExcludedFolders = _viewMode == LibraryViewMode.Folder ? Array.Empty<string>() : _excludedFolders.ToArray()
    };

    private async void QueueProjectionRefresh()
    {
        if (_projectionQueued || _lifetime.IsCancellationRequested) return;
        _projectionQueued = true;
        try
        {
            await Task.Delay(750, _lifetime.Token);
            while (_isProjecting) await Task.Delay(100, _lifetime.Token);
            await RebuildRowsAsync();
        }
        catch (OperationCanceledException) { }
        finally { _projectionQueued = false; }
    }

    private PhotoItem CreateVisibleItem(SavedMediaItem saved, long index)
    {
        var item = _selection.Find(saved.Path)
            ?? new PhotoItem(saved.Path, saved.SizeBytes, saved.FileModifiedAt);
        item.IsFavorite = saved.IsFavorite; item.ViewIndex = index;
        if (saved.MetadataIndexed) item.ApplyIndexedCaptureDate(saved.CaptureDate);
        return item;
    }

    private async Task RebuildRowsAsync()
    {
        if (!_catalogLoaded) return;
        _projectionCancellation?.Cancel();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _projectionCancellation = operation;
        var token = operation.Token;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var query = CreateQuery();
        var identity = $"{query.Folder}|{query.ViewMode}|{query.SearchText}|{query.ShowVideos}|{query.IncludeSubfolders}|{query.MissingCaptureDateOnly}";
        var preserve = identity == _viewIdentity;
        var anchor = preserve ? GetGridAnchor() : 0;
        var old = PhotoRows as VirtualPhotoRows;
        _isProjecting = true;
        if (!preserve)
        {
            old?.Dispose(); PhotoRows = Array.Empty<PhotoRow>(); OnPropertyChanged(nameof(PhotoRows));
        }
        EmptyState.Visibility = Visibility.Collapsed;
        StatusText.Text = "Обновляю вид…";
        CatalogProgressBar.Visibility = Visibility.Visible;
        CatalogProgressBar.IsIndeterminate = true;
        try
        {
            await Task.Delay(60, token);
            var groups = await _desktopCatalogStore.QueryGroupsAsync(query, token);
            token.ThrowIfCancellationRequested();
            var rows = new VirtualPhotoRows(_desktopCatalogStore, query, groups, _columns, TileImageHeight + 60,
                _collapsedDateGroups, CreateVisibleItem, token);
            rows.LoadFailed += message => { if (ReferenceEquals(PhotoRows, rows)) StatusText.Text = $"Страница не загружена: {message}"; };
            await rows.PrimeAsync();
            if (token.IsCancellationRequested) { rows.Dispose(); token.ThrowIfCancellationRequested(); }
            old?.Dispose(); PhotoRows = rows; _currentQuery = query; _viewIdentity = identity;
            OnPropertyChanged(nameof(PhotoRows));
            if (preserve && anchor > 0 && rows.Count > 0)
            {
                var row = rows[rows.RowForItem(anchor)];
                PhotoGrid.ScrollIntoView(row);
            }
            StatusText.Text = $"В виде: {rows.ItemCount:N0} · Каталог: {_catalogCount:N0}";
            PerformanceMetrics.Record("catalog.first_page", timer.Elapsed.TotalMilliseconds, rows.ItemCount);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Не удалось обновить вид: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_projectionCancellation, operation))
            {
                _projectionCancellation = null; _isProjecting = false;
                CatalogProgressBar.Visibility = _isCatalogLoading ? Visibility.Visible : Visibility.Collapsed;
                EmptyState.Visibility = !_isCatalogLoading && PhotoRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private long GetGridAnchor()
    {
        var scroll = FindVisualChild<System.Windows.Controls.ScrollViewer>(PhotoGrid);
        if (scroll is null || PhotoRows is not VirtualPhotoRows rows || rows.Count == 0) return 0;
        return rows.ItemIndexForRow(Math.Clamp((int)scroll.VerticalOffset, 0, rows.Count - 1));
    }
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private Task BrowseFolderAsync(string folder)
    {
        if (_fileOperationActive || _hasPendingRecovery || !_catalogLoaded || _closing) return Task.CompletedTask;
        _browseCancellation?.Cancel();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var previous = _browseTask;
        _browseCancellation = operation;
        return _browseTask = RunBrowseAsync(folder, operation, _includeSystemFolders, _includeSubfolders, previous);
    }
    private async Task RunBrowseAsync(string folder, CancellationTokenSource operation, bool system, bool recursive, Task previous)
    {
        try
        {
            await previous;
            operation.Token.ThrowIfCancellationRequested();
            await PhotoScanner.ScanAsync(new[] { folder }, batch => IndexScanBatchAsync(batch, operation.Token),
                system, operation.Token, recursive: recursive);
            if (!operation.IsCancellationRequested) { await RefreshCatalogCountAsync(); QueueProjectionRefresh(); StartMetadataIndexing(false); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!operation.IsCancellationRequested) StatusText.Text = ex.Message; }
        finally { if (ReferenceEquals(_browseCancellation, operation)) _browseCancellation = null; operation.Dispose(); }
    }

    private async Task IndexScanBatchAsync(PhotoScanBatch batch, CancellationToken token)
    {
        var items = await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Scan, _ =>
            batch.Paths.Select(ToSavedItem).Where(x => x is not null).Cast<SavedMediaItem>().ToArray(), token);
        token.ThrowIfCancellationRequested();
        await _desktopCatalogStore.UpsertItemsAsync(items, preserveFavorites: true, token);
        await Dispatcher.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested) return;
            StatusText.Text = $"Сканирую: {batch.SeenCount:N0} · {batch.CurrentFolder}";
            QueueProjectionRefresh();
        }, DispatcherPriority.Background, token);
    }
    private static SavedMediaItem? ToSavedItem(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            var hidden = false;
            string? folder = file.DirectoryName;
            while (!string.IsNullOrEmpty(folder) && folder != Path.GetPathRoot(folder))
            {
                try { hidden |= (File.GetAttributes(folder) & (FileAttributes.Hidden | FileAttributes.System)) != 0; }
                catch (IOException) { break; }
                folder = Path.GetDirectoryName(folder);
            }
            hidden |= (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
            hidden |= PhotoScanner.IsIgnoredPath(path, false);
            return new SavedMediaItem { Path = path, SizeBytes = file.Length, FileModifiedAt = file.LastWriteTime,
                IsVideo = PhotoItem.IsVideoPath(path), IsHiddenOrSystem = hidden };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private async Task RefreshCatalogCountAsync()
    {
        _catalogCount = await _desktopCatalogStore.CountAsync(new CatalogViewQuery { IncludeSystemFolders = true }, _lifetime.Token);
    }
    private async void RefreshTreeVisibility()
    {
        var revision = ++_treeVisibilityRevision;
        var nodes = _folderNodes.Values.ToArray(); var includeSystem = _includeSystemFolders;
        try
        {
            var hidden = await Task.Run(() => nodes.Select(n => n.FullPath != Path.GetPathRoot(n.FullPath) && ShouldHideFolderInTree(n.FullPath, includeSystem)).ToArray(), _lifetime.Token);
            for (var i = 0; i < nodes.Length; i++)
            {
                if (revision != _treeVisibilityRevision || _lifetime.IsCancellationRequested) return;
                nodes[i].IsVisible = !hidden[i];
                if (i % 64 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Дерево: {ex.Message}"; }
    }

    private void OnSubfoldersChanged(object sender, RoutedEventArgs e)
    {
        _includeSubfolders = SubfoldersCheckBox.IsChecked == true;
        RebuildRows();
        if (_viewMode == LibraryViewMode.Folder && _activeFolder is not null) _ = BrowseFolderAsync(_activeFolder);
        SaveCatalogState();
    }

    private LocalCatalogState CaptureState() => new()
    {
        TileWidth = TileWidth, ShowVideos = ShowVideos, IncludeSystemFolders = _includeSystemFolders,
        DateGroupingMode = _dateGroupingMode.ToString(), ActiveFolder = _activeFolder, ViewMode = _viewMode.ToString(),
        SortNewestFirst = _sortNewestFirst, IncludeSubfolders = _includeSubfolders,
        ExpandedFolders = _folderNodes.Values.Where(x => x.IsExpanded).Select(x => x.FullPath).ToList(),
        ExcludedFolders = _excludedFolders.ToList()
    };
    private async Task SaveAfterDelayAsync(CancellationToken token)
    {
        try { await Task.Delay(500, token); await PersistStateAsync(CaptureState(), token, 0); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Настройки не сохранены: {ex.Message}"; }
    }
    private async Task PersistStateAsync(LocalCatalogState state, CancellationToken token, long revision)
    {
        await _saveGate.WaitAsync(token);
        try { await _desktopCatalogStore.SaveAsync(state, token, saveItems: false); }
        finally { _saveGate.Release(); }
    }
    private async Task StopCatalogWritersAsync(bool stopDuplicates = true)
    {
        _scanCancellation?.Cancel(); _browseCancellation?.Cancel();
        _metadataIndexCancellation?.Cancel(); _maintenanceCancellation?.Cancel();
        _saveCancellation?.Cancel(); _infoCancellation?.Cancel();
        if (stopDuplicates)
        {
            _duplicateCancellation?.Cancel();
            try { await _duplicateWorkTask; } catch (OperationCanceledException) { }
        }
        try { await Task.WhenAll(_infoWorkTasks.ToArray()); } catch (Exception) { /* All readers have finished, including canceled or failed reads. */ }
        await Task.WhenAll(_scanTask, _browseTask, _metadataTask, _maintenanceTask, _pendingSave);
    }
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeReady) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        var enabledOwnedWindows = OwnedWindows.Cast<Window>().Where(window => window.IsEnabled).ToArray();
        foreach (var window in enabledOwnedWindows) window.IsEnabled = false;
        _fileOperationCancellation?.Cancel(); _duplicateCancellation?.Cancel();
        try
        {
            await StopCatalogWritersAsync();
            if (_activeDuplicateReview is { } review) { review.StopOperation(); await review.PendingOperation; }
            await _fileOperationTask;
            if (_catalogLoaded) await PersistStateAsync(CaptureState(), CancellationToken.None, 0);
            _closeReady = true; Close();
        }
        catch (Exception ex)
        {
            _closing = false;
            IsEnabled = true;
            foreach (var window in enabledOwnedWindows.Where(window => window.IsLoaded)) window.IsEnabled = true;
            System.Windows.MessageBox.Show($"Не удалось завершить сохранение: {ex.Message}\nОкно оставлено открытым.", "PhotoShelf");
        }
    }
}
