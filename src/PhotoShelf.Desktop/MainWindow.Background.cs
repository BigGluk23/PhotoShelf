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
    private CancellationTokenSource? _rangeSelectionCancellation;
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
    private readonly CatalogRefreshBuffer _backgroundRefresh = new();
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

    private void NoteCatalogChanged()
    {
        _backgroundRefresh.RecordChange();
        UpdatePendingRefresh();
        QueueProjectionRefresh();
    }

    private void CompleteCatalogStage()
    {
        _backgroundRefresh.CompleteStage();
        QueueProjectionRefresh();
    }

    private void UpdatePendingRefresh()
    {
        RefreshViewButton.Visibility = _backgroundRefresh.HasPendingChanges ? Visibility.Visible : Visibility.Collapsed;
        RefreshViewButton.IsEnabled = !_isProjecting && !_fileOperationActive;
    }

    private async void OnRefreshViewClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOperationActive || _closing) return;
        await RebuildRowsAsync();
    }

    private async void QueueProjectionRefresh()
    {
        if (_projectionQueued || _lifetime.IsCancellationRequested || _closing || _fileOperationActive ||
            !_backgroundRefresh.ShouldPublish((PhotoRows as VirtualPhotoRows)?.ItemCount > 0)) return;
        _projectionQueued = true;
        try
        {
            // Only an empty view or an explicit worker boundary can reach publication.
            // Ordinary scan/metadata batches leave the current ItemsSource untouched.
            do
            {
                await Task.Delay(250, _lifetime.Token);
                while (_isProjecting) await Task.Delay(100, _lifetime.Token);
                if (_closing || _fileOperationActive || !_backgroundRefresh.ShouldPublish((PhotoRows as VirtualPhotoRows)?.ItemCount > 0)) break;
                if (!await RebuildRowsAsync(background: true)) break;
            }
            while (_backgroundRefresh.ShouldPublish((PhotoRows as VirtualPhotoRows)?.ItemCount > 0));
        }
        catch (OperationCanceledException) { }
        finally { _projectionQueued = false; UpdatePendingRefresh(); }
    }

    private PhotoItem CreateVisibleItem(SavedMediaItem saved, long index) => CreateVisibleItem(saved, index, null);

    private PhotoItem CreateVisibleItem(SavedMediaItem saved, long index, IReadOnlyDictionary<string, PhotoItem>? retained)
    {
        var item = _selection.Find(saved.Path) ?? retained?.GetValueOrDefault(saved.Path)
            ?? new PhotoItem(saved.Path, saved.SizeBytes, saved.FileModifiedAt);
        item.ApplyFileInformation(saved.SizeBytes, saved.FileModifiedAt);
        item.IsFavorite = saved.IsFavorite; if (index >= 0) item.ViewIndex = index;
        item.ApplyIndexedCaptureDate(saved.CaptureDate, saved.MetadataStatus);
        return item;
    }

    private async Task<bool> RebuildRowsAsync(bool background = false)
    {
        if (!_catalogLoaded) return false;
        _rangeSelectionCancellation?.Cancel();
        _projectionCancellation?.Cancel();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _projectionCancellation = operation;
        var token = operation.Token;
        var revision = _backgroundRefresh.Revision;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var query = CreateQuery();
        var identity = $"{query.Folder}|{query.ViewMode}|{query.SearchText}|{query.ShowVideos}|{query.IncludeSubfolders}|{query.MissingCaptureDateOnly}";
        var preserve = identity == _viewIdentity;
        var anchor = preserve ? GetGridAnchor() : new GridAnchor(0, null);
        var old = PhotoRows as VirtualPhotoRows;
        // The cache is bounded by VirtualPhotoRows. Never materialize the whole catalog.
        var retained = preserve ? old?.LoadedItems.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase) : null;
        VirtualPhotoRows? pending = null;
        _isProjecting = true;
        UpdatePendingRefresh();
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
            var anchorIndex = anchor.Index;
            if (anchor.Path is not null)
                anchorIndex = await _desktopCatalogStore.IndexOfAsync(query, anchor.Path, token) ?? anchorIndex;
            token.ThrowIfCancellationRequested();
            var rows = pending = new VirtualPhotoRows(_desktopCatalogStore, query, groups, _columns, TileImageHeight + 60,
                _collapsedDateGroups, (saved, index) => CreateVisibleItem(saved, index, retained), token);
            rows.LoadFailed += message => { if (ReferenceEquals(PhotoRows, rows)) StatusText.Text = $"Страница не загружена: {message}"; };
            await rows.PrimeAsync(anchorIndex);
            token.ThrowIfCancellationRequested();
            // Do not undo a scroll made while the background query was preparing.
            // The explicit update button remains available for this pending revision.
            if (background && preserve && GetGridAnchor() != anchor) return false;
            old?.Dispose(); PhotoRows = rows; pending = null; _currentQuery = query; _viewIdentity = identity;
            OnPropertyChanged(nameof(PhotoRows));
            if (preserve && anchorIndex > 0 && rows.Count > 0)
            {
                var row = rows[rows.RowForItem(Math.Min(anchorIndex, rows.ItemCount - 1))];
                PhotoGrid.ScrollIntoView(row);
                // ScrollIntoView only guarantees visibility; restore the anchor as the top row.
                if (FindVisualChild<System.Windows.Controls.ScrollViewer>(PhotoGrid) is { } scroll)
                    scroll.ScrollToVerticalOffset(rows.RowForItem(Math.Min(anchorIndex, rows.ItemCount - 1)));
            }
            _backgroundRefresh.Published(revision);
            StatusText.Text = $"В виде: {rows.ItemCount:N0} · Каталог: {_catalogCount:N0}";
            PerformanceMetrics.Record("catalog.first_page", timer.Elapsed.TotalMilliseconds, rows.ItemCount);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (ReferenceEquals(_projectionCancellation, operation)) StatusText.Text = $"Не удалось обновить вид: {ex.Message}";
            return false;
        }
        finally
        {
            pending?.Dispose();
            if (ReferenceEquals(_projectionCancellation, operation))
            {
                _projectionCancellation = null; _isProjecting = false;
                CatalogProgressBar.Visibility = _isCatalogLoading ? Visibility.Visible : Visibility.Collapsed;
                EmptyState.Visibility = !_isCatalogLoading && PhotoRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdatePendingRefresh();
            }
        }
    }

    private sealed record GridAnchor(long Index, string? Path);
    private GridAnchor GetGridAnchor()
    {
        var scroll = FindVisualChild<System.Windows.Controls.ScrollViewer>(PhotoGrid);
        if (scroll is null || PhotoRows is not VirtualPhotoRows rows || rows.Count == 0) return new(0, null);
        var rowIndex = Math.Clamp((int)scroll.VerticalOffset, 0, rows.Count - 1);
        // Headers have no media path; their first cached photo row is the same visual anchor.
        var path = rows.LoadedPathForRow(rowIndex) ?? (rowIndex + 1 < rows.Count ? rows.LoadedPathForRow(rowIndex + 1) : null);
        return new(rows.ItemIndexForRow(rowIndex), path);
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
            if (!operation.IsCancellationRequested) { await RefreshCatalogCountAsync(); StartMetadataIndexing(false); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!operation.IsCancellationRequested) StatusText.Text = ex.Message; }
        finally { if (ReferenceEquals(_browseCancellation, operation)) { _browseCancellation = null; CompleteCatalogStage(); } operation.Dispose(); }
    }

    private async Task IndexScanBatchAsync(PhotoScanBatch batch, CancellationToken token)
    {
        var items = await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Scan, _ =>
            batch.Paths.Select(ToSavedItem).Where(x => x is not null).Cast<SavedMediaItem>().ToArray(), token);
        token.ThrowIfCancellationRequested();
        await _desktopCatalogStore.UpsertItemsAsync(items, preserveFavorites: true, token);
        await Dispatcher.InvokeAsync(() =>
        {
            if (_lifetime.IsCancellationRequested || _closing) return;
            if (!token.IsCancellationRequested) StatusText.Text = $"Сканирую: {batch.SeenCount:N0} · {batch.CurrentFolder}";
            NoteCatalogChanged();
        }, DispatcherPriority.Background);
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
        _saveCancellation?.Cancel(); _infoCancellation?.Cancel(); _rangeSelectionCancellation?.Cancel();
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
            if (ErrorReporter.AutomatedCheck)
            {
                ErrorReporter.Show(ex, "UI smoke shutdown failed");
                System.Windows.Application.Current.Shutdown(1);
                return;
            }
            _closing = false;
            IsEnabled = true;
            foreach (var window in enabledOwnedWindows.Where(window => window.IsLoaded)) window.IsEnabled = true;
            System.Windows.MessageBox.Show($"Не удалось завершить сохранение: {ex.Message}\nОкно оставлено открытым.", "PhotoShelf");
        }
    }
}
