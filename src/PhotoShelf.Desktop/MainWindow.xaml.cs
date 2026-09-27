using System.Collections.ObjectModel;
using System.Collections;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Background;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PhotoShelf.Infrastructure.Sqlite;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PhotoShelf.Desktop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const string VersionLabel = "Ultra v0.10.1";
    private readonly Dictionary<string, FolderNode> _folderNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly LocalCatalogState _catalogState;
    private readonly MetadataIndexStore _metadataIndexStore = new();
    private readonly SqliteDesktopCatalogStore _desktopCatalogStore = new();
    private readonly DuplicateHashStore _duplicateHashStore = new();
    private readonly Forms.NotifyIcon _trayIcon;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _duplicateCancellation;
    private CancellationTokenSource? _metadataIndexCancellation;
    private int _columns = 5;
    private string? _activeFolder;
    private string _searchText = string.Empty;
    private double _tileWidth = 178;
    private bool _showVideos = true;
    private bool _includeSystemFolders;
    private bool _isInitializing = true;
    private bool _catalogLoaded;
    private bool _isCatalogLoading;
    private readonly DispatcherTimer _folderFilterRefreshTimer;
    private bool _sortNewestFirst = true;
    private DateGroupingMode _dateGroupingMode = DateGroupingMode.FileDate;
    private bool _showOnlyMissingCaptureDate;
    private PhotoItem? _selectedPhoto;
    private string? _duplicateCompareFolderA;
    private string? _duplicateCompareFolderB;
    private LibraryViewMode _viewMode = LibraryViewMode.All;
    private readonly HashSet<string> _collapsedDateGroups = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(LocalCatalogState initialState)
    {
        InitializeComponent();
        Title = $"PhotoShelf {VersionLabel}";
        AppTitleText.Text = $"PhotoShelf {VersionLabel}";
        DataContext = this;
        _catalogState = initialState;
        TileWidth = Math.Clamp(_catalogState.TileWidth, 72, 260);
        ThumbnailSizeSlider.Value = TileWidth;
        ShowVideos = _catalogState.ShowVideos;
        ShowVideosCheckBox.IsChecked = ShowVideos;
        _includeSystemFolders = _catalogState.IncludeSystemFolders;
        IncludeSystemFoldersCheckBox.IsChecked = _includeSystemFolders;
        _dateGroupingMode = Enum.TryParse<DateGroupingMode>(_catalogState.DateGroupingMode, out var savedMode) ? savedMode : DateGroupingMode.FileDate;
        _sortNewestFirst = _catalogState.SortNewestFirst;
        _activeFolder = _catalogState.ActiveFolder;
        _viewMode = Enum.TryParse<LibraryViewMode>(_catalogState.ViewMode, out var view) ? view : LibraryViewMode.All;
        _includeSubfolders = _catalogState.IncludeSubfolders;
        SubfoldersCheckBox.IsChecked = _includeSubfolders;
        foreach (var folder in _catalogState.ExcludedFolders) _excludedFolders.Add(NormalizePath(folder));
        DateModeBox.SelectedIndex = _dateGroupingMode == DateGroupingMode.CaptureDate ? 0 : 1;
        RefreshDateSortButton();
        FolderTree.ItemsSource = FolderRoots;
        RefreshChrome();
        _trayIcon = CreateTrayIcon();
        _folderFilterRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _folderFilterRefreshTimer.Tick += OnFolderFilterRefreshTimerTick;

        if (_activeFolder is not null) AddFolder(_activeFolder);
        foreach (var path in _catalogState.ExpandedFolders) AddFolder(path);
        ViewTitleText.Text = _viewMode == LibraryViewMode.Folder ? _activeFolder : _viewMode switch
        { LibraryViewMode.Favorites => "Избранное", LibraryViewMode.Recent => "Последние", _ => "Все фотографии" };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IList PhotoRows { get; private set; } = Array.Empty<PhotoRow>();

    public ObservableCollection<FolderNode> FolderRoots { get; } = new();

    public double TileWidth
    {
        get => _tileWidth;
        private set
        {
            if (Math.Abs(_tileWidth - value) < 0.1)
            {
                return;
            }

            _tileWidth = value;
            OnPropertyChanged(nameof(TileWidth));
            OnPropertyChanged(nameof(TileImageHeight));
        }
    }

    public double TileImageHeight => Math.Round(TileWidth * 0.71);

    public bool ShowVideos
    {
        get => _showVideos;
        private set
        {
            if (_showVideos == value)
            {
                return;
            }

            _showVideos = value;
            OnPropertyChanged(nameof(ShowVideos));
        }
    }

    private double TileOuterWidth => TileWidth + 16;

    private async void OnAddFolderClicked(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Выберите папку с фотографиями",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        await ScanRootsAsync(new[] { dialog.SelectedPath });
    }

    private async void OnDiscoverPhotosClicked(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "PhotoShelf просканирует все готовые локальные диски, включая C:. Это может занять время.",
            "Найти все фото",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        await ScanRootsAsync(await Task.Run(PhotoScanner.GetDefaultDiscoveryRoots));
    }

    private async void OnDuplicatesClicked(object sender, RoutedEventArgs e)
    {
        var scope = await AskDuplicateScopeAsync();
        if (scope is null)
        {
            return;
        }

        await FindExactDuplicatesAsync(scope.Value);
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(ShowVideos, _includeSystemFolders)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ShowVideos = dialog.ShowVideos;
        _includeSystemFolders = dialog.IncludeSystemFolders;
        ShowVideosCheckBox.IsChecked = ShowVideos;
        IncludeSystemFoldersCheckBox.IsChecked = _includeSystemFolders;
        RefreshTreeVisibility();
        RebuildRows();
        RefreshChrome();
        SaveCatalogState();
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        _isInitializing = false;
        PerformanceMetrics.Start(Dispatcher);
        StartSavedCatalogLoading();
    }

    private void OnCancelDuplicatesClicked(object sender, RoutedEventArgs e)
    {
        _duplicateCancellation?.Cancel();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _trayIcon.Visible = true;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text.Trim();
        RebuildRows();
        RefreshChrome();
    }

    private void OnAllPhotosClicked(object sender, RoutedEventArgs e)
    {
        _browseCancellation?.Cancel();
        _activeFolder = null;
        _viewMode = LibraryViewMode.All;
        FolderTree.Focus();
        ViewTitleText.Text = "Все фотографии";
        RebuildRows();
        RefreshChrome();
    }

    private void OnFavoritesClicked(object sender, RoutedEventArgs e)
    {
        _browseCancellation?.Cancel();
        _activeFolder = null;
        _viewMode = LibraryViewMode.Favorites;
        FolderTree.Focus();
        ViewTitleText.Text = "Избранное";
        RebuildRows();
        RefreshChrome();
    }

    private void OnRecentClicked(object sender, RoutedEventArgs e)
    {
        _browseCancellation?.Cancel();
        _activeFolder = null;
        _viewMode = LibraryViewMode.Recent;
        FolderTree.Focus();
        ViewTitleText.Text = "Последние";
        RebuildRows();
        RefreshChrome();
    }

    private void OnDateSortClicked(object sender, RoutedEventArgs e)
    {
        _sortNewestFirst = !_sortNewestFirst;
        RefreshDateSortButton();
        RebuildRows();
        RefreshChrome();
    }

    private void OnDateModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (DateModeBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<DateGroupingMode>(tag, out var mode))
        {
            _dateGroupingMode = mode;
            RefreshDateSortButton();
            RebuildRows();
            RefreshChrome();
            SaveCatalogState();
        }
    }

    private void OnMissingCaptureDateFilterClicked(object sender, RoutedEventArgs e)
    {
        _showOnlyMissingCaptureDate = !_showOnlyMissingCaptureDate;
        MissingCaptureDateFilterButton.Content = _showOnlyMissingCaptureDate ? "Без даты съёмки ✓" : "Без даты съёмки";
        RebuildRows();
        RefreshChrome();
    }

    private void OnCancelScanClicked(object sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
    }

    private void OnReindexMetadataClicked(object sender, RoutedEventArgs e)
    {
        StartMetadataIndexing(resetExisting: true);
    }

    private void OnShowVideosChanged(object sender, RoutedEventArgs e)
    {
        ShowVideos = ShowVideosCheckBox.IsChecked == true;
        if (_isInitializing)
        {
            return;
        }

        RebuildRows();
        SaveCatalogState();
    }

    private void OnIncludeSystemFoldersChanged(object sender, RoutedEventArgs e)
    {
        _includeSystemFolders = IncludeSystemFoldersCheckBox.IsChecked == true;
        if (_isInitializing)
        {
            return;
        }

        RefreshTreeVisibility();
        foreach (var node in _folderNodes.Values) node.ChildrenLoaded = false;
        QueueFolderFilterRefresh();
    }

    private void OnFolderTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FolderNode folderNode)
        {
            return;
        }

        LoadImmediateSubfolders(folderNode);
        ViewTitleText.Text = folderNode.FullPath;
        _activeFolder = folderNode.FullPath;
        _viewMode = LibraryViewMode.Folder;
        RebuildRows();
        _ = BrowseFolderAsync(folderNode.FullPath);
        RefreshChrome();
    }

    private void OnFolderTreeItemExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: FolderNode folderNode })
        {
            LoadImmediateSubfolders(folderNode);
        }
    }

    private void OnFolderIncludedChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || sender is not System.Windows.Controls.CheckBox { DataContext: FolderNode node } || node.IsPlaceholder) return;
        _scanCancellation?.Cancel();
        _excludedFolders.RemoveWhere(x => IsUnderFolder(x, node.FullPath));
        if (node.IsIncluded) _excludedFolders.RemoveWhere(x => IsUnderFolder(node.FullPath, x));
        else _excludedFolders.Add(node.FullPath);
        _ = RefreshFolderChecksAsync();
        if (node.IsIncluded) _ = ScanRootsAsync(new[] { node.FullPath }, changeView: false);
        QueueFolderFilterRefresh();
        e.Handled = true;
    }

    private int _folderCheckRevision;
    private async Task RefreshFolderChecksAsync()
    {
        var revision = ++_folderCheckRevision;
        var nodes = _folderNodes.Values.ToArray();
        var excluded = _excludedFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var included = await Task.Run(() => nodes.Select(node =>
        {
            string? current = node.FullPath;
            while (!string.IsNullOrEmpty(current))
            {
                if (excluded.Contains(NormalizePath(current))) return false;
                current = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar));
            }
            return true;
        }).ToArray());
        for (var index = 0; index < nodes.Length; index++)
        {
            if (revision != _folderCheckRevision || _lifetime.IsCancellationRequested) return;
            nodes[index].IsIncluded = included[index];
            if (index % 64 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
        }
    }

    private void QueueFolderFilterRefresh()
    {
        _folderFilterRefreshTimer.Stop();
        _folderFilterRefreshTimer.Start();
        StatusText.Text = "Обновляю выбранные папки...";
    }

    private void OnFolderFilterRefreshTimerTick(object? sender, EventArgs e)
    {
        _folderFilterRefreshTimer.Stop();
        RebuildRows();
        RefreshChrome();
        SaveCatalogState();
    }

    private void OnShowInfoChanged(object sender, RoutedEventArgs e)
    {
        var show = ShowInfoCheckBox.IsChecked == true;
        InfoColumn.Width = show ? new GridLength(320) : new GridLength(0);
        InfoPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateInfoPanel();
    }

    private async void OnPhotoTileMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PhotoItem item }) return;
        if (_isProjecting || _fileOperationActive) { StatusText.Text = _fileOperationActive ? "Выполняется безопасный перенос…" : "Обновляю порядок файлов…"; return; }
        _dragStart = e.GetPosition(this);
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _selectedPhoto is not null && _currentQuery is not null)
        {
            var start = Math.Min(_selectedPhoto.ViewIndex, item.ViewIndex);
            var count = Math.Abs(_selectedPhoto.ViewIndex - item.ViewIndex) + 1;
            var query = _currentQuery;
            try
            {
                for (long offset = 0; offset < count; offset += 256)
                {
                    var page = await _desktopCatalogStore.QueryPageAsync(query, checked((int)(start + offset)), (int)Math.Min(256, count - offset), token: _lifetime.Token);
                    if (!ReferenceEquals(query, _currentQuery)) break;
                    for (var i = 0; i < page.Items.Count; i++) { var selected = CreateVisibleItem(page.Items[i], start + offset + i); selected.IsSelected = true; _selection.Add(selected); }
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
                RefreshLoadedSelection();
            }
            catch (Exception ex) { StatusText.Text = $"Выделение: {ex.Message}"; }
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var selected = _selection.Find(item.Path);
            if (selected is null) _selection.Add(item); else { _selection.Remove(selected); selected.IsSelected = false; }
            item.IsSelected = selected is null;
        }
        else if (!_selection.Contains(item))
        {
            foreach (var selected in _selection) selected.IsSelected = false;
            _selection.Clear(); _selection.Add(item); item.IsSelected = true; RefreshLoadedSelection();
        }
        _selectedPhoto = item; SelectedText.Text = $"Выбрано: {_selection.Count:N0}"; UpdateInfoPanel();
        if (e.ClickCount == 2) OpenViewer(item);
    }


    private void OnDateHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PhotoRow { IsHeader: true, GroupKey: { } groupKey } row })
        {
            return;
        }

        if (row.IsCollapsed)
        {
            _collapsedDateGroups.Remove(groupKey);
        }
        else
        {
            _collapsedDateGroups.Add(groupKey);
        }

        RebuildRows();
        RefreshChrome();
        e.Handled = true;
    }

    private async void OpenViewer(PhotoItem selected)
    {
        if (_fileOperationActive || _isProjecting || _currentQuery is null || PhotoRows is not VirtualPhotoRows rows || rows.ItemCount == 0) return;
        var query = _currentQuery;
        try
        {
            var index = await _desktopCatalogStore.IndexOfAsync(query, selected.Path, _lifetime.Token);
            if (index is null || !ReferenceEquals(query, _currentQuery)) return;
            var viewer = new PhotoViewerWindow(_desktopCatalogStore, query, rows.ItemCount, index.Value, selected.Path) { Owner = this };
            viewer.Show();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Просмотр: {ex.Message}"; }
    }


    private void OnPhotoContextOpenClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PhotoItem item })
        {
            return;
        }

        SelectPhoto(item);
        OpenViewer(item);
    }

    private void OnPhotoContextOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PhotoItem item })
        {
            return;
        }

        SelectPhoto(item);
        OnOpenFolderClicked(sender, e);
    }

    private void OnPhotoContextFavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PhotoItem item })
        {
            return;
        }

        SelectPhoto(item);
        OnFavoriteActionClicked(sender, e);
    }

    private void OnPhotoContextInfoClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PhotoItem item })
        {
            return;
        }

        SelectPhoto(item);
        ShowInfoCheckBox.IsChecked = true;
        OnShowInfoChanged(sender, e);
    }

    private void OnFolderContextOpenInExplorerClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderNode node })
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{node.FullPath}\"",
            UseShellExecute = true
        });
    }

    private async void OnFolderContextScanClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderNode node })
        {
            return;
        }

        await ScanRootsAsync(new[] { node.FullPath });
    }

    private async void OnFolderContextDuplicatesClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderNode node })
        {
            return;
        }

        _activeFolder = node.FullPath;
        _viewMode = LibraryViewMode.Folder;
        ViewTitleText.Text = node.FullPath;
        RebuildRows();
        RefreshChrome();
        await FindExactDuplicatesAsync(DuplicateSearchScope.CurrentFolder);
    }

    private Task ScanRootsAsync(IEnumerable<string> roots, bool changeView = true)
    {
        if (_fileOperationActive || _hasPendingRecovery || !_catalogLoaded || _closing) return Task.CompletedTask;
        _scanCancellation?.Cancel();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var previous = _scanTask;
        _scanCancellation = operation;
        return _scanTask = RunScanAsync(roots.ToArray(), changeView, operation, previous);
    }
    private async Task RunScanAsync(string[] roots, bool changeView, CancellationTokenSource operation, Task previous)
    {
        try
        {
            await previous;
            var token = operation.Token; token.ThrowIfCancellationRequested();
            SetScanning(true);
            foreach (var root in roots) AddFolder(root);
            if (changeView) { _activeFolder = null; _viewMode = LibraryViewMode.All; ViewTitleText.Text = "Все фотографии"; RebuildRows(); }
            await PhotoScanner.ScanAsync(roots, batch => IndexScanBatchAsync(batch, token), _includeSystemFolders, token, _excludedFolders.ToArray());
            await RefreshCatalogCountAsync(); QueueProjectionRefresh(); SaveCatalogState(); StartMetadataIndexing(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Ошибка сканирования: {ex.Message}"; }
        finally { if (ReferenceEquals(_scanCancellation, operation)) { _scanCancellation = null; SetScanning(false); } operation.Dispose(); }
    }


    private void RefreshLoadedSelection()
    {
        if (PhotoRows is not VirtualPhotoRows rows) return;
        foreach (var item in rows.LoadedItems)
            item.IsSelected = _selection.Contains(item);
    }


    private void OnPhotoGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var newColumns = CalculateColumnCount(e.NewSize.Width);
        if (newColumns == _columns)
        {
            return;
        }

        _columns = newColumns;
        RebuildRows();
    }

    private void OnThumbnailSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        TileWidth = e.NewValue;
        if (_isInitializing)
        {
            return;
        }

        var newColumns = CalculateColumnCount(PhotoGrid.ActualWidth);
        if (newColumns != _columns)
        {
            _columns = newColumns;
            RebuildRows();
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        var delta = e.Delta > 0 ? 12 : -12;
        ThumbnailSizeSlider.Value = Math.Clamp(ThumbnailSizeSlider.Value + delta, ThumbnailSizeSlider.Minimum, ThumbnailSizeSlider.Maximum);
        e.Handled = true;
    }

    private async void OnFavoriteActionClicked(object sender, RoutedEventArgs e)
    {
        var item = _selectedPhoto;
        if (item is null || _fileOperationActive) return;
        var favorite = !item.IsFavorite;
        item.IsFavorite = favorite;
        try
        {
            await _desktopCatalogStore.SetFavoriteAsync(item.Path, favorite, _lifetime.Token);
            if (_viewMode == LibraryViewMode.Favorites) RebuildRows();
        }
        catch (Exception ex) { item.IsFavorite = !favorite; StatusText.Text = $"Избранное не сохранено: {ex.Message}"; }
    }


    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedPhoto is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{_selectedPhoto.Path}\"",
            UseShellExecute = true
        });
    }

    private void SetScanning(bool isScanning)
    {
        CancelScanButton.IsEnabled = isScanning;
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        (PhotoRows as VirtualPhotoRows)?.Dispose();
        PerformanceMetrics.Stop();
        _scanCancellation?.Cancel();
        _duplicateCancellation?.Cancel();
        _metadataIndexCancellation?.Cancel();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnClosed(e);
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var trayIcon = new Forms.NotifyIcon
        {
            Text = $"PhotoShelf {VersionLabel}",
            Icon = AppResources.CreateTrayIcon(),
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        trayIcon.ContextMenuStrip.Items.Add("Показать", null, (_, _) => ShowFromTray());
        trayIcon.ContextMenuStrip.Items.Add("Выход", null, (_, _) => Close());
        trayIcon.DoubleClick += (_, _) => ShowFromTray();
        return trayIcon;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void AddFolder(string folder, bool incrementDirectCount = false)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var normalized = NormalizePath(folder);
        if (_folderNodes.TryGetValue(normalized, out var existing))
        {
            if (incrementDirectCount)
            {
                existing.DirectItemCount++;
            }

            return;
        }

        var root = Path.GetPathRoot(normalized);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var rootNode = EnsureFolderNode(root, parent: null);
        rootNode.IsExpanded = _catalogState.ExpandedFolders.Contains(root, StringComparer.OrdinalIgnoreCase);

        var currentPath = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = rootNode;
        var remainder = normalized[root.Length..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(remainder))
        {
            foreach (var part in remainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                currentPath = currentPath + Path.DirectorySeparatorChar + part;
                parent = EnsureFolderNode(currentPath, parent);
            }
        }

        if (incrementDirectCount)
        {
            parent.DirectItemCount++;
        }
    }

    private FolderNode EnsureFolderNode(string fullPath, FolderNode? parent)
    {
        var normalized = NormalizePath(fullPath);
        if (_folderNodes.TryGetValue(normalized, out var node))
        {
            return node;
        }

        node = new FolderNode(normalized) { IsIncluded = !_excludedFolders.Contains(normalized) && (parent?.IsIncluded ?? true) };
        node.Children.Add(FolderNode.Placeholder());
        node.IsExpanded = _catalogState.ExpandedFolders.Contains(normalized, StringComparer.OrdinalIgnoreCase);
        _folderNodes.Add(normalized, node);

        var collection = parent?.Children ?? FolderRoots;
        if (collection.Count == 1 && collection[0].IsPlaceholder) collection.Clear();
        var low = 0; var high = collection.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (string.Compare(collection[middle].Name, node.Name, StringComparison.CurrentCultureIgnoreCase) <= 0) low = middle + 1;
            else high = middle;
        }
        var insertIndex = low;
        collection.Insert(insertIndex, node);
        return node;
    }

    private async void LoadImmediateSubfolders(FolderNode parent)
    {
        if (parent.IsPlaceholder || parent.IsLoading || parent.ChildrenLoaded) return;
        parent.IsLoading = true;
        var includeSystem = _includeSystemFolders;
        try
        {
            var cached = _catalogLoaded ? await _desktopCatalogStore.GetImmediateFoldersAsync(parent.FullPath, _lifetime.Token) : Array.Empty<string>();
            var directories = await Task.Run(() =>
            {
                var children = new HashSet<string>(cached, StringComparer.OrdinalIgnoreCase);
                try { foreach (var directory in Directory.EnumerateDirectories(parent.FullPath)) children.Add(directory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return children.Where(directory => !ShouldHideFolderInTree(directory, includeSystem)).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
            }, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            if (parent.Children.Count == 1 && parent.Children[0].IsPlaceholder) parent.Children.Clear();
            foreach (var batch in directories.Chunk(32))
            {
                foreach (var directory in batch) EnsureFolderNode(directory, parent);
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (_lifetime.IsCancellationRequested) return;
            }
            parent.ChildrenLoaded = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        finally { parent.IsLoading = false; }
    }

    private bool ShouldHideFolderInTree(string directory, bool includeSystemFolders)
    {
        if (PhotoScanner.IsIgnoredPath(directory, includeSystemFolders))
        {
            return true;
        }

        if (includeSystemFolders)
        {
            return false;
        }

        try
        {
            var attributes = File.GetAttributes(directory);
            return attributes.HasFlag(FileAttributes.Hidden) ||
                   attributes.HasFlag(FileAttributes.System) ||
                   attributes.HasFlag(FileAttributes.Temporary);
        }
        catch
        {
            return false;
        }
    }

    private bool ShouldHideFolderInTree(string directory)
    {
        return ShouldHideFolderInTree(directory, _includeSystemFolders);
    }

    private void RefreshChrome()
    {
        EmptyState.Visibility = !_isProjecting && !_isCatalogLoading && PhotoRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_isProjecting && !_isCatalogLoading) StatusText.Text = $"В виде: {(PhotoRows as VirtualPhotoRows)?.ItemCount ?? 0:N0} · Каталог: {_catalogCount:N0}";
    }


    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }



    private bool IsFolderIncluded(string folder) => !_excludedFolders.Any(excluded => IsUnderFolder(folder, excluded));

    private bool IsVisibleByType(PhotoItem item)
    {
        return ShowVideos || !item.IsVideo;
    }

    private void RebuildRows()
    {
        if (_isInitializing || _lifetime.IsCancellationRequested) return;
        _ = RebuildRowsAsync();
    }

    private void RefreshDateSortButton()
    {
        var label = _dateGroupingMode == DateGroupingMode.CaptureDate ? "Съёмка" : "Файл";
        DateSortButton.Content = _sortNewestFirst ? $"{label} ↓" : $"{label} ↑";
    }

    private int CalculateColumnCount(double width)
    {
        return Math.Clamp((int)(Math.Max(1, width - 28) / TileOuterWidth), 1, 10);
    }

    public static async Task<LocalCatalogState> LoadInitialCatalogStateAsync()
    {
        return await Task.Run(async () =>
        {
            var store = new SqliteDesktopCatalogStore();
            await store.InitializeAsync();
            var state = await store.LoadAsync(includeItems: false);
            if (!state.ReadItemsFromSqlite) state = LocalCatalogStore.Load();
            return state;
        });
    }

    private void StartMetadataIndexing(bool resetExisting)
    {
        if (_fileOperationActive || _hasPendingRecovery || !_catalogLoaded || _closing) return;
        _metadataIndexCancellation?.Cancel();
        var previous = _metadataTask;
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _metadataIndexCancellation = operation;
        _metadataTask = RunMetadataWorkerAsync(resetExisting, operation, previous);
    }


    private async Task RunMetadataWorkerAsync(bool resetExisting, CancellationTokenSource operation, Task previous)
    {
        try
        {
            await previous;
            var token = operation.Token; token.ThrowIfCancellationRequested();
            MetadataStatusText.Text = "Индексирую метаданные…";
            await Task.Run(async () =>
            {
                var indexed = 0;
                await foreach (var item in _desktopCatalogStore.EnumerateAsync(new CatalogViewQuery { IncludeSystemFolders = true }, token))
                {
                    if (item.IsVideo || (!resetExisting && item.MetadataIndexed)) continue;
                    var fresh = await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Metadata,
                        _ => ToSavedItem(item.Path), token);
                    if (fresh is null) continue; // Disconnected media stays in the catalog.
                    var date = await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Metadata,
                        _ => PhotoItem.ReadCaptureDate(item.Path), token);
                    await _desktopCatalogStore.UpdateCaptureDateAsync(item.Path, fresh.SizeBytes, fresh.FileModifiedAt, date, token);
                    if (++indexed % 128 == 0)
                    {
                        var count = indexed;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            MetadataStatusText.Text = $"Метаданные: {count:N0}";
                            if (_dateGroupingMode == DateGroupingMode.CaptureDate) QueueProjectionRefresh();
                        }, DispatcherPriority.Background, token);
                    }
                }
            }, token);
            if (!token.IsCancellationRequested) { MetadataStatusText.Text = "Метаданные: готово"; QueueProjectionRefresh(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MetadataStatusText.Text = $"Метаданные: {ex.Message}"; }
        finally { if (ReferenceEquals(_metadataIndexCancellation, operation)) _metadataIndexCancellation = null; operation.Dispose(); }
    }


    private async Task LoadSavedCatalogAsync()
    {
        await _desktopCatalogStore.InitializeAsync(_lifetime.Token);
        await _metadataIndexStore.InitializeAsync(_lifetime.Token);
        await _duplicateHashStore.InitializeAsync(_lifetime.Token);
        if (!_catalogState.ReadItemsFromSqlite && _catalogState.Items.Count > 0)
        {
            foreach (var batch in _catalogState.Items.Chunk(128))
            {
                var restored = await Task.Run(() => batch.Select(saved =>
                {
                    var current = ToSavedItem(saved.Path) ?? saved; current.IsFavorite = saved.IsFavorite; return current;
                }).ToArray(), _lifetime.Token);
                await _desktopCatalogStore.UpsertItemsAsync(restored, preserveFavorites: false, _lifetime.Token);
            }
            _catalogState.Items.Clear();
        }
        await CheckPendingOperationsAsync();
        _catalogLoaded = true;
        await PersistStateAsync(CaptureState(), _lifetime.Token, 0);
        var roots = await Task.Run(() => DriveInfo.GetDrives().Select(x => x.Name).ToArray(), _lifetime.Token);
        foreach (var root in roots) AddFolder(root);
        await RefreshCatalogCountAsync();
        await RebuildRowsAsync();
        await CheckPendingOperationsAsync();
    }


    private void StartSavedCatalogLoading()
    {
        _isCatalogLoading = true;
        CatalogProgressBar.Visibility = Visibility.Visible;
        CatalogProgressBar.Minimum = 0;
        CatalogProgressBar.Maximum = Math.Max(1, _catalogState.Items.Count);
        CatalogProgressBar.Value = 0;
        CatalogProgressBar.IsIndeterminate = true;
        StatusText.Text = "Каталог загружается в фоне...";
        _ = LoadSavedCatalogInBackgroundAsync();
    }

    private async Task LoadSavedCatalogInBackgroundAsync()
    {
        try
        {
            await LoadSavedCatalogAsync();
            StartBackgroundCatalogMaintenance();
            if (!_hasPendingRecovery) StartMetadataIndexing(resetExisting: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Не удалось загрузить каталог: {ex.Message}"; }
        finally
        {
            _isCatalogLoading = false;
            CatalogProgressBar.IsIndeterminate = false;
            CatalogProgressBar.Visibility = Visibility.Collapsed;
            RefreshChrome();
        }
    }

    private void StartBackgroundCatalogMaintenance()
    {
        if (_fileOperationActive || _hasPendingRecovery || _closing) return;
        _maintenanceCancellation?.Cancel();
        var previous = _maintenanceTask;
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _maintenanceCancellation = operation;
        _maintenanceTask = MaintainCatalogAsync(operation, previous);
    }
    private async Task MaintainCatalogAsync(CancellationTokenSource operation, Task previous)
    {
        try
        {
            await previous;
            var token = operation.Token;
            var changed = new List<SavedMediaItem>(128);
            await foreach (var old in _desktopCatalogStore.EnumerateAsync(new CatalogViewQuery { IncludeSystemFolders = true }, token))
            {
                var fresh = await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Scan, _ => ToSavedItem(old.Path), token);
                if (fresh is not null && (fresh.SizeBytes != old.SizeBytes || fresh.FileModifiedAt != old.FileModifiedAt || fresh.IsHiddenOrSystem != old.IsHiddenOrSystem)) changed.Add(fresh);
                if (changed.Count < 128) continue;
                await _desktopCatalogStore.UpsertItemsAsync(changed, preserveFavorites: true, token); changed.Clear();
            }
            if (changed.Count > 0) await _desktopCatalogStore.UpsertItemsAsync(changed, preserveFavorites: true, token);
            QueueProjectionRefresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Проверка каталога: {ex.Message}"; }
        finally { if (ReferenceEquals(_maintenanceCancellation, operation)) _maintenanceCancellation = null; operation.Dispose(); }
    }


    private void SaveCatalogState()
    {
        if (_fileOperationActive || _isInitializing || !_catalogLoaded || _isCatalogLoading || _closing) return;
        _saveCancellation?.Cancel();
        _saveCancellation = new CancellationTokenSource();
        _pendingSave = SaveAfterDelayAsync(_saveCancellation.Token);
    }

    private async Task FindExactDuplicatesAsync(DuplicateSearchScope scope)
    {
        if (_duplicateCancellation is not null || _fileOperationActive || !_catalogLoaded || _closing) return;
        if (_hasPendingRecovery)
        {
            System.Windows.MessageBox.Show("Сначала завершите восстановление в окне «Операции». Это необходимо перед новой обработкой дублей.", "Восстановление");
            return;
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _duplicateCancellation = operation;
        var token = operation.Token;
        var compareA = _duplicateCompareFolderA; var compareB = _duplicateCompareFolderB;
        var query = scope == DuplicateSearchScope.CurrentView ? CreateQuery() : new CatalogViewQuery
        {
            IncludeSystemFolders = _includeSystemFolders, ShowVideos = ShowVideos,
            Folder = scope == DuplicateSearchScope.CurrentFolder ? _activeFolder : null,
            ExcludedFolders = scope == DuplicateSearchScope.IncludedFolders ? _excludedFolders.ToArray() : Array.Empty<string>()
        };
        query = query with { DuplicateCandidatesOnly = true };
        SetDuplicateSearch(true, 1); DuplicateProgressBar.IsIndeterminate = true;
        StatusText.Text = "Подготавливаю поиск дублей…";
        try
        {
            var searchTask = Task.Run(async () =>
            {
                var hashes = new Dictionary<(long Size, string Hash), List<PhotoItem>>();
                var pending = new List<SavedDuplicateHash>(); var count = 0;
                await foreach (var saved in _desktopCatalogStore.EnumerateAsync(query, token))
                {
                    if (scope == DuplicateSearchScope.CompareTwoFolders &&
                        !(compareA is not null && IsUnderFolder(saved.Path, compareA)) &&
                        !(compareB is not null && IsUnderFolder(saved.Path, compareB))) continue;
                    var fresh = await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Hash, _ => ToSavedItem(saved.Path), token);
                    if (fresh is null || fresh.SizeBytes <= 0) continue;
                    var cached = await _duplicateHashStore.TryGetAsync(fresh.Path, fresh.SizeBytes, fresh.FileModifiedAt, token);
                    var hash = cached?.Hash;
                    if (string.IsNullOrEmpty(hash))
                    {
                        try
                        {
                            hash = await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Hash, async ct =>
                            {
                                await using var stream = new FileStream(fresh.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                                var value = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                                var after = new FileInfo(fresh.Path);
                                return after.Length == fresh.SizeBytes && after.LastWriteTime == fresh.FileModifiedAt ? value : null;
                            }, token);
                            if (hash is null) continue;
                            pending.Add(new SavedDuplicateHash { Path = fresh.Path, SizeBytes = fresh.SizeBytes, FileModifiedAt = fresh.FileModifiedAt, Hash = hash });
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    }
                    if (!hashes.TryGetValue((fresh.SizeBytes, hash), out var entries)) hashes[(fresh.SizeBytes, hash)] = entries = new();
                    entries.Add(new PhotoItem(fresh.Path, fresh.SizeBytes, fresh.FileModifiedAt));
                    if (++count % 128 == 0)
                    {
                        await _duplicateHashStore.SaveAsync(pending.ToArray(), token); pending.Clear();
                        var progress = count;
                        await Dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) StatusText.Text = $"Проверено кандидатов: {progress:N0}"; }, DispatcherPriority.Background, token);
                    }
                }
                await _duplicateHashStore.SaveAsync(pending, token);
                return hashes.Where(x => x.Value.Count > 1)
                    .Where(x => scope != DuplicateSearchScope.CompareTwoFolders || (compareA is not null && compareB is not null && x.Value.Any(i => IsUnderFolder(i.Path, compareA)) && x.Value.Any(i => IsUnderFolder(i.Path, compareB))))
                    .Select(x => new DuplicateGroup(x.Key.Size, x.Key.Hash, x.Value)).ToArray();
            }, token);
            _duplicateWorkTask = searchTask;
            var groups = await searchTask;
            token.ThrowIfCancellationRequested();
            if (groups.Length == 0) System.Windows.MessageBox.Show("Точных дублей не найдено.", "Дубликаты");
            else
            {
                var models = await Task.Run(() => new ObservableCollection<DuplicateGroupViewModel>(groups.Select((group, index) => new DuplicateGroupViewModel(group, index + 1))), token);
                if (_fileOperationActive || token.IsCancellationRequested) return;
                _fileOperationActive = true;
                try
                {
                    await StopCatalogWritersAsync(stopDuplicates: false);
                    var review = new DuplicateReviewWindow(models, CommitFileMoveAsync) { Owner = this };
                    _activeDuplicateReview = review;
                    foreach (var viewer in OwnedWindows.OfType<PhotoViewerWindow>().ToArray()) viewer.Close();
                    review.ShowDialog();
                }
                finally
                {
                    ApplyMovedPaths(); _activeDuplicateReview = null;
                    try { await RefreshCatalogCountAsync(); await CheckPendingOperationsAsync(); RebuildRows(); }
                    finally { _fileOperationActive = false; }
                }
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "Поиск дублей отменён"; }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Поиск дублей"); }
        finally { if (ReferenceEquals(_duplicateCancellation, operation)) _duplicateCancellation = null; SetDuplicateSearch(false, 0); }
    }
    private async Task<DuplicateSearchScope?> AskDuplicateScopeAsync()
    {
        if (!_catalogLoaded) return null;
        var all = await _desktopCatalogStore.CountAsync(new CatalogViewQuery { ShowVideos = ShowVideos, IncludeSystemFolders = _includeSystemFolders }, _lifetime.Token);
        var included = await _desktopCatalogStore.CountAsync(new CatalogViewQuery { ShowVideos = ShowVideos, IncludeSystemFolders = _includeSystemFolders, ExcludedFolders = _excludedFolders.ToArray() }, _lifetime.Token);
        var dialog = new DuplicateSearchDialog((int)Math.Min(int.MaxValue, (PhotoRows as VirtualPhotoRows)?.ItemCount ?? 0), (int)Math.Min(int.MaxValue, included), (int)Math.Min(int.MaxValue, all),
            _viewMode == LibraryViewMode.Folder ? _activeFolder : null) { Owner = this };
        if (dialog.ShowDialog() != true) return null;
        _duplicateCompareFolderA = dialog.CompareFolderA; _duplicateCompareFolderB = dialog.CompareFolderB;
        return dialog.SelectedScope;
    }

    private static bool IsUnderFolder(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(path);
        var normalizedFolder = Path.GetFullPath(folder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedPath.Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedFolder + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void SetDuplicateSearch(bool isSearching, int total)
    {
        DuplicatesButton.IsEnabled = !isSearching;
        CancelDuplicatesButton.Visibility = isSearching ? Visibility.Visible : Visibility.Collapsed;
        DuplicateProgressBar.Visibility = isSearching ? Visibility.Visible : Visibility.Collapsed;
        DuplicateProgressBar.Minimum = 0;
        DuplicateProgressBar.Maximum = Math.Max(1, total);
        DuplicateProgressBar.Value = 0;
    }

    private void SelectPhoto(PhotoItem item)
    {
        if (ReferenceEquals(_selectedPhoto, item))
        {
            return;
        }

        if (_selectedPhoto is not null)
        {
            _selectedPhoto.IsSelected = false;
        }

        _selectedPhoto = item;
        _selectedPhoto.IsSelected = true;
        SelectedText.Text = item.FileName;
        UpdateInfoPanel();
    }

    private async void UpdateInfoPanel()
    {
        _infoCancellation?.Cancel();
        if (InfoPanel.Visibility != Visibility.Visible || _fileOperationActive || _closing) return;
        var item = _selectedPhoto;
        var revision = ++_infoRevision;
        InfoText.Text = item is null ? "Выберите фото или видео." : "Читаю сведения…";
        if (item is null) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _infoCancellation = operation;
        Task? pendingRead = null;
        try
        {
            var work = BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.Interactive, _ => item.MetadataText, operation.Token);
            pendingRead = work;
            _infoWorkTasks.Add(work);
            var text = await work;
            if (!operation.IsCancellationRequested && revision == _infoRevision && ReferenceEquals(item, _selectedPhoto)) InfoText.Text = text;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (revision == _infoRevision) InfoText.Text = $"Сведения недоступны: {ex.Message}"; }
        finally
        {
            if (pendingRead is not null) _infoWorkTasks.Remove(pendingRead);
            if (ReferenceEquals(_infoCancellation, operation)) _infoCancellation = null;
        }
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string FormatPhotoCount(int count)
    {
        var suffix = count % 10 == 1 && count % 100 != 11
            ? "фотография"
            : count % 10 is >= 2 and <= 4 && count % 100 is < 10 or >= 20
                ? "фотографии"
                : "фотографий";
        return $"{count} {suffix}";
    }

    private enum LibraryViewMode
    {
        All,
        Folder,
        Favorites,
        Recent
    }

}
