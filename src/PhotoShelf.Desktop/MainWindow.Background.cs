using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PhotoShelf.Desktop;

public partial class MainWindow
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _excludedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _projectionCancellation;
    private CancellationTokenSource? _browseCancellation;
    private CancellationTokenSource? _saveCancellation;
    private Task _pendingSave = Task.CompletedTask;
    private PhotoItem[] _visiblePhotos = Array.Empty<PhotoItem>();
    private bool _isProjecting;
    private bool _projectionQueued;
    private bool _includeSubfolders = true;
    private bool _closeReady;
    private bool _closing;
    private int _infoRevision;
    private long _catalogRevision;
    private long _persistedCatalogRevision = -1;

    private async void QueueProjectionRefresh()
    {
        if (_projectionQueued || _lifetime.IsCancellationRequested) return;
        _projectionQueued = true;
        try
        {
            if (PhotoRows.Count > 0 || _isProjecting) await Task.Delay(750, _lifetime.Token);
            // Finish the current projection so sustained scanning cannot starve the first page.
            while (_isProjecting) await Task.Delay(100, _lifetime.Token);
            RebuildRows();
        }
        catch (OperationCanceledException) { }
        finally { _projectionQueued = false; }
    }

    private async Task RebuildRowsAsync()
    {
        _projectionCancellation?.Cancel();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _projectionCancellation = operation;
        var token = operation.Token;
        _isProjecting = true;
        PhotoRows.Clear();
        _visiblePhotos = Array.Empty<PhotoItem>();
        EmptyState.Visibility = Visibility.Collapsed;
        StatusText.Text = "Обновляю вид…";
        CatalogProgressBar.Visibility = Visibility.Visible;
        CatalogProgressBar.IsIndeterminate = true;
        var source = Photos.ToArray();
        var includeSystem = _includeSystemFolders;
        var excluded = _excludedFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mode = _viewMode;
        var folder = _activeFolder;
        var search = _searchText;
        var showVideos = ShowVideos;
        var missing = _showOnlyMissingCaptureDate;
        var dateMode = _dateGroupingMode;
        var descending = _sortNewestFirst;
        var recursive = _includeSubfolders;
        var columns = _columns;
        var collapsed = _collapsedDateGroups.ToHashSet();
        try
        {
            await Task.Delay(60, token);
            var result = await Task.Run(() =>
            {
                var eligible = new List<PhotoItem>();
                var allowedFolders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var includedFolders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in source)
                {
                    token.ThrowIfCancellationRequested();
                    if (!showVideos && item.IsVideo) continue;
                    if (!includeSystem)
                    {
                        if (!allowedFolders.TryGetValue(item.Folder, out var allowed))
                        {
                            allowed = !PhotoScanner.IsIgnoredPath(item.Folder, false);
                            var current = item.Folder;
                            while (allowed && !string.IsNullOrEmpty(current) && current != Path.GetPathRoot(current))
                            {
                                try { allowed = (File.GetAttributes(current) & (FileAttributes.Hidden | FileAttributes.System)) == 0; }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
                                current = Path.GetDirectoryName(current);
                            }
                            allowedFolders[item.Folder] = allowed;
                        }
                        if (!allowed) continue;
                    }
                    if (mode == LibraryViewMode.Folder && folder is not null)
                    {
                        if (!(recursive ? IsUnderFolder(item.Path, folder) : NormalizePath(item.Folder).Equals(NormalizePath(folder), StringComparison.OrdinalIgnoreCase))) continue;
                    }
                    else
                    {
                        if (!includedFolders.TryGetValue(item.Folder, out var included))
                        {
                            included = true;
                            var current = item.Folder;
                            while (!string.IsNullOrEmpty(current))
                            {
                                if (excluded.Contains(NormalizePath(current))) { included = false; break; }
                                current = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar));
                            }
                            includedFolders[item.Folder] = included;
                        }
                        if (!included) continue;
                    }
                    if (mode == LibraryViewMode.Favorites && !item.IsFavorite) continue;
                    if (missing && (item.IsVideo || item.CaptureDate is not null)) continue;
                    if (search.Length > 0 && !item.FileName.Contains(search, StringComparison.CurrentCultureIgnoreCase) && !item.Folder.Contains(search, StringComparison.CurrentCultureIgnoreCase)) continue;
                    eligible.Add(item);
                }
                IEnumerable<PhotoItem> query = eligible;
                if (mode == LibraryViewMode.Recent) query = query.OrderByDescending(x => x.FileModifiedAt).Take(500);
                DateTime? Date(PhotoItem x) => dateMode == DateGroupingMode.CaptureDate ? x.CaptureDate : x.FileModifiedAt;
                var ordered = (descending ? query.OrderByDescending(Date) : query.OrderBy(Date))
                    .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                token.ThrowIfCancellationRequested();
                var rows = new List<PhotoRow>();
                foreach (var group in ordered.GroupBy(x => (Date(x)?.Year, Date(x)?.Month)))
                {
                    token.ThrowIfCancellationRequested();
                    var (year, month) = group.Key;
                    var key = $"{(dateMode == DateGroupingMode.CaptureDate ? "capture" : "file")}:{(year is null ? "none" : $"{year:D4}-{month:D2}")}";
                    var culture = CultureInfo.GetCultureInfo("ru-RU");
                    var title = year is null ? (dateMode == DateGroupingMode.CaptureDate ? "Без даты съёмки" : "Без даты файла")
                        : $"{year} / {culture.TextInfo.ToTitleCase(culture.DateTimeFormat.GetMonthName(month!.Value))}";
                    rows.Add(PhotoRow.CreateHeader(title, key, collapsed.Contains(key)));
                    if (collapsed.Contains(key)) continue;
                    foreach (var chunk in group.Chunk(columns)) { token.ThrowIfCancellationRequested(); rows.Add(new PhotoRow(chunk)); }
                }
                return (ordered, rows);
            }, token);
            token.ThrowIfCancellationRequested();
            _visiblePhotos = result.ordered;
            foreach (var batch in result.rows.Chunk(24))
            {
                token.ThrowIfCancellationRequested();
                foreach (var row in batch) PhotoRows.Add(row);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            StatusText.Text = $"Показано: {result.ordered.Length} · Всего: {Photos.Count}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Не удалось обновить вид: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_projectionCancellation, operation))
            {
                _projectionCancellation = null;
                _isProjecting = false;
                CatalogProgressBar.Visibility = _isCatalogLoading ? Visibility.Visible : Visibility.Collapsed;
                EmptyState.Visibility = !_isCatalogLoading && PhotoRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private async Task BrowseFolderAsync(string folder)
    {
        _browseCancellation?.Cancel();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _browseCancellation = operation;
        var token = operation.Token;
        var includeSystem = _includeSystemFolders;
        var recursive = _includeSubfolders;
        try
        {
            await PhotoScanner.ScanAsync(new[] { folder }, async batch =>
            {
                var items = batch.Paths.Select(path => new PhotoItem(path)).ToArray();
                await Dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) ApplyItems(items); }, DispatcherPriority.Background, token);
            }, includeSystem, token, recursive: recursive);
            token.ThrowIfCancellationRequested();
            RebuildRows();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) StatusText.Text = ex.Message; }
        finally { if (ReferenceEquals(_browseCancellation, operation)) _browseCancellation = null; }
    }

    private async void RefreshTreeVisibility()
    {
        var nodes = _folderNodes.Values.ToArray(); var includeSystem = _includeSystemFolders;
        var hidden = await Task.Run(() => nodes.Select(n => n.FullPath != Path.GetPathRoot(n.FullPath) && ShouldHideFolderInTree(n.FullPath, includeSystem)).ToArray());
        for (var i = 0; i < nodes.Length; i++)
        {
            nodes[i].IsVisible = !hidden[i];
            if (i % 64 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
        }
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
        ExcludedFolders = _excludedFolders.ToList(),
        Items = Photos.Select(x => new SavedMediaItem { Path = x.Path, IsFavorite = x.IsFavorite, IsVideo = x.IsVideo, SizeBytes = x.FileSizeBytes, FileModifiedAt = x.FileModifiedAt }).ToList(),
        DuplicateHashes = _duplicateHashCache.Values.ToList()
    };

    private async Task SaveAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(500, token);
            var state = CaptureState();
            await PersistStateAsync(state, token, _catalogRevision);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Каталог не сохранён: {ex.Message}"; }
    }

    private async Task PersistStateAsync(LocalCatalogState state, CancellationToken token, long revision)
    {
        await _saveGate.WaitAsync(token);
        try
        {
            await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                await _desktopCatalogStore.SaveAsync(state, token, saveItems: revision != _persistedCatalogRevision);
                // The fallback is written only after a successful database transaction.
                LocalCatalogStore.Save(state);
                _persistedCatalogRevision = revision;
            }, token);
        }
        finally { _saveGate.Release(); }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeReady) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _saveCancellation?.Cancel();
        _scanCancellation?.Cancel();
        _browseCancellation?.Cancel();
        _metadataIndexCancellation?.Cancel();
        _duplicateCancellation?.Cancel();
        try
        {
            await _fileOperationTask;
            await _pendingSave;
            if (_catalogLoaded && !_isCatalogLoading) await PersistStateAsync(CaptureState(), CancellationToken.None, _catalogRevision);
            _closeReady = true;
            Close();
        }
        catch (Exception ex)
        {
            _closing = false;
            System.Windows.MessageBox.Show($"Не удалось сохранить каталог: {ex.Message}\nОкно оставлено открытым.", "PhotoShelf");
        }
    }
}
