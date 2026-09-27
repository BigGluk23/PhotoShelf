using System.Collections.ObjectModel;
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
    private const string VersionLabel = "v0.9.7";
    private readonly Dictionary<string, FolderNode> _folderNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PhotoItem> _itemsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownPhotoPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SavedDuplicateHash> _duplicateHashCache = new(StringComparer.OrdinalIgnoreCase);
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
        foreach (var hash in _catalogState.DuplicateHashes)
        {
            if (!string.IsNullOrWhiteSpace(hash.Path) && !string.IsNullOrWhiteSpace(hash.Hash))
            {
                _duplicateHashCache[hash.Path] = hash;
            }
        }
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
        foreach (var root in DriveInfo.GetDrives()) AddFolder(root.Name);
        if (_activeFolder is not null) AddFolder(_activeFolder);
        foreach (var path in _catalogState.ExpandedFolders) AddFolder(path);
        ViewTitleText.Text = _viewMode == LibraryViewMode.Folder ? _activeFolder : _viewMode switch
        { LibraryViewMode.Favorites => "Избранное", LibraryViewMode.Recent => "Последние", _ => "Все фотографии" };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PhotoItem> Photos { get; } = new();

    public ObservableCollection<PhotoRow> PhotoRows { get; } = new();

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

    private void OnPhotoTileMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PhotoItem item })
        {
            return;
        }

        _dragStart = e.GetPosition(this);
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _selectedPhoto is not null)
        {
            var start = Array.IndexOf(_visiblePhotos, _selectedPhoto);
            var end = Array.IndexOf(_visiblePhotos, item);
            if (start >= 0 && end >= 0) foreach (var selected in _visiblePhotos.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1))
            { _selection.Add(selected); selected.IsSelected = true; }
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (!_selection.Add(item)) _selection.Remove(item);
            item.IsSelected = _selection.Contains(item);
        }
        else if (!_selection.Contains(item))
        {
            foreach (var selected in _selection) selected.IsSelected = false;
            _selection.Clear(); _selection.Add(item); item.IsSelected = true;
        }
        _selectedPhoto = item;
        SelectedText.Text = $"Выбрано: {_selection.Count}";
        UpdateInfoPanel();

        if (e.ClickCount == 2)
        {
            OpenViewer(item);
        }
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

    private void OpenViewer(PhotoItem selected)
    {
        var visibleItems = _visiblePhotos;
        var index = Array.IndexOf(visibleItems, selected);
        var viewer = new PhotoViewerWindow(visibleItems, Math.Max(0, index))
        {
            Owner = this
        };
        viewer.Show();
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

    private async Task ScanRootsAsync(IEnumerable<string> roots, bool changeView = true)
    {
        _scanCancellation?.Cancel();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _scanCancellation = operation;
        var token = operation.Token;
        SetScanning(true);
        var rootList = roots.ToArray();
        foreach (var root in rootList) AddFolder(root);
        if (changeView)
        {
            _activeFolder = null;
            _viewMode = LibraryViewMode.All;
            ViewTitleText.Text = "Все фотографии";
            RebuildRows();
        }
        var includeSystem = _includeSystemFolders;
        var excluded = _excludedFolders.ToArray();
        try
        {
            await PhotoScanner.ScanAsync(rootList, async batch =>
            {
                token.ThrowIfCancellationRequested();
                var items = batch.Paths.Select(path => new PhotoItem(path)).ToArray();
                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    ApplyItems(items);
                    StatusText.Text = $"Сканирую: {batch.CurrentFolder} · {Photos.Count}";
                }, DispatcherPriority.Background, token);
            }, includeSystem, token, excluded);
            RebuildRows();
            SaveCatalogState();
            StartMetadataIndexing(resetExisting: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Ошибка сканирования: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_scanCancellation, operation))
            {
                _scanCancellation = null;
                SetScanning(false);
            }
        }
    }

    private void ApplyItems(IReadOnlyList<PhotoItem> items)
    {
        foreach (var item in items)
        {
            if (!_knownPhotoPaths.Add(item.Path))
            {
                if (!_itemsByPath.TryGetValue(item.Path, out var old) || (old.FileSizeBytes == item.FileSizeBytes && old.FileModifiedAt == item.FileModifiedAt)) continue;
                item.IsFavorite = old.IsFavorite;
                var index = Photos.IndexOf(old);
                if (index >= 0) Photos[index] = item;
                _itemsByPath[item.Path] = item;
                _duplicateHashCache.Remove(item.Path);
                _catalogRevision++;
                continue;
            }
            _itemsByPath[item.Path] = item;
            Photos.Add(item);
            _catalogRevision++;
            AddFolder(item.Folder, incrementDirectCount: true);
        }
        QueueProjectionRefresh();
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

    private void OnFavoriteActionClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedPhoto is null)
        {
            return;
        }

        _selectedPhoto.IsFavorite = !_selectedPhoto.IsFavorite;
        _catalogRevision++;
        if (_viewMode == LibraryViewMode.Favorites && !_selectedPhoto.IsFavorite)
        {
            RebuildRows();
        }

        SaveCatalogState();
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
        _scanCancellation?.Cancel();
        _duplicateCancellation?.Cancel();
        _metadataIndexCancellation?.Cancel();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnClosed(e);
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "PhotoShelf.ico");
        var trayIcon = new Forms.NotifyIcon
        {
            Text = "PhotoShelf",
            Icon = File.Exists(iconPath) ? new Drawing.Icon(iconPath) : Drawing.SystemIcons.Application,
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
        var insertIndex = 0;
        while (insertIndex < collection.Count &&
               string.Compare(collection[insertIndex].Name, node.Name, StringComparison.CurrentCultureIgnoreCase) <= 0)
        {
            insertIndex++;
        }

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
            var directories = await Task.Run(() => Directory.EnumerateDirectories(parent.FullPath)
                .Where(directory => !ShouldHideFolderInTree(directory, includeSystem))
                .Order(StringComparer.CurrentCultureIgnoreCase).ToArray(), _lifetime.Token);
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
            return true;
        }
    }

    private bool ShouldHideFolderInTree(string directory)
    {
        return ShouldHideFolderInTree(directory, _includeSystemFolders);
    }

    private void RefreshChrome()
    {
        EmptyState.Visibility = !_isProjecting && !_isCatalogLoading && PhotoRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_isProjecting && !_isCatalogLoading) StatusText.Text = FormatPhotoCount(Photos.Count);
        OnPropertyChanged(nameof(Photos));
        OnPropertyChanged(nameof(PhotoRows));
        OnPropertyChanged(nameof(FolderRoots));
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private IEnumerable<PhotoItem> GetVisiblePhotos() => _visiblePhotos;

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
        _metadataIndexCancellation?.Cancel();
        _metadataIndexCancellation = new CancellationTokenSource();
        var token = _metadataIndexCancellation.Token;

        var items = Photos
            .Where(item => !item.IsVideo)
            .Where(item => resetExisting || !item.IsCaptureDateLoaded)
            .ToArray();

        if (items.Length == 0)
        {
            MetadataStatusText.Text = "Метаданные: готово";
            return;
        }

        MetadataStatusText.Text = $"Метаданные: 0 / {items.Length}";
        _ = RunMetadataWorkerAsync(items, token);
    }

    private async Task RunMetadataWorkerAsync(PhotoItem[] items, CancellationToken token)
    {
        try { await Task.Run(async () =>
        {
            var indexed = 0;
            var changedSinceUiRefresh = 0;
            foreach (var item in items)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                var captureDate = PhotoItem.ReadCaptureDate(item.Path);
                var changed = item.CaptureDate != captureDate;
                await Dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) item.ApplyIndexedCaptureDate(captureDate); }, DispatcherPriority.Background, token);
                try
                {
                    await _metadataIndexStore.SaveAsync(item.Path, item.FileSizeBytes, item.FileModifiedAt, item.CaptureDate, token).ConfigureAwait(false);
                }
                catch
                {
                    // Индексация не должна ломать просмотр.
                }

                indexed++;
                if (changed)
                {
                    changedSinceUiRefresh++;
                }

                if (indexed % 250 == 0 || indexed == items.Length)
                {
                    var current = indexed;
                    var shouldRebuild = _dateGroupingMode == DateGroupingMode.CaptureDate && changedSinceUiRefresh > 0;
                    changedSinceUiRefresh = 0;
                    await Dispatcher.BeginInvoke(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        MetadataStatusText.Text = $"Метаданные: {current} / {items.Length}";
                        if (shouldRebuild)
                        {
                            QueueProjectionRefresh();
                        }

                        UpdateInfoPanel();
                    });
                }
            }

            if (!token.IsCancellationRequested)
            {
                await Dispatcher.BeginInvoke(() =>
                {
                    if (token.IsCancellationRequested) return;
                    MetadataStatusText.Text = "Метаданные: готово";
                    if (_dateGroupingMode == DateGroupingMode.CaptureDate)
                    {
                        RebuildRows();
                        RefreshChrome();
                    }
                });
            }
        }, token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MetadataStatusText.Text = $"Метаданные: {ex.Message}"; }
    }

    private async Task LoadSavedCatalogAsync()
    {
        await Task.Run(async () =>
        {
            await _desktopCatalogStore.InitializeAsync();
            await _metadataIndexStore.InitializeAsync();
            await _duplicateHashStore.InitializeAsync();
            var dates = await _metadataIndexStore.LoadCaptureDatesAsync(_lifetime.Token);
            var hashes = await _duplicateHashStore.LoadAsync(_lifetime.Token);
            await Dispatcher.InvokeAsync(() => { foreach (var hash in hashes) _duplicateHashCache[hash.Path] = hash; });
            async Task ApplyBatch(IReadOnlyList<SavedMediaItem> saved)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var items = saved.Where(x => !string.IsNullOrWhiteSpace(x.Path) && PhotoItem.IsSupported(x.Path))
                    .Select(x =>
                    {
                        var item = _catalogState.ReadItemsFromSqlite ? new PhotoItem(x.Path, x.SizeBytes, x.FileModifiedAt) : new PhotoItem(x.Path);
                        item.IsFavorite = x.IsFavorite;
                        if (dates.TryGetValue(x.Path, out var cached) && cached.Size == item.FileSizeBytes && cached.ModifiedTicks == (item.FileModifiedAt?.ToUniversalTime().Ticks ?? 0))
                            item.ApplyIndexedCaptureDate(cached.CaptureDate);
                        return item;
                    }).ToArray();
                await Dispatcher.InvokeAsync(() => ApplyItems(items), DispatcherPriority.Background, _lifetime.Token);
            }
            if (_catalogState.ReadItemsFromSqlite) await _desktopCatalogStore.ReadBatchesAsync(ApplyBatch, _lifetime.Token);
            else foreach (var batch in _catalogState.Items.Chunk(128)) await ApplyBatch(batch);
        }, _lifetime.Token);
        _catalogLoaded = true;
        RebuildRows();
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
            StartMetadataIndexing(resetExisting: false);
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

    private async void StartBackgroundCatalogMaintenance()
    {
        var snapshot = Photos.ToArray();
        try
        {
            await Task.Run(async () =>
            {
                foreach (var batch in snapshot.Chunk(128))
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    var changed = new List<PhotoItem>();
                    foreach (var old in batch)
                    {
                        try
                        {
                            var file = new FileInfo(old.Path);
                            if (file.Exists && (file.Length != old.FileSizeBytes || file.LastWriteTime != old.FileModifiedAt))
                                changed.Add(new PhotoItem(old.Path, file.Length, file.LastWriteTime));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    if (changed.Count > 0) await Dispatcher.InvokeAsync(() =>
                    {
                        // A file operation may have removed a path since this snapshot.
                        if (!_fileOperationActive) ApplyItems(changed.Where(x => _knownPhotoPaths.Contains(x.Path)).ToArray());
                    }, DispatcherPriority.Background, _lifetime.Token);
                }
            }, _lifetime.Token);
            if (!_fileOperationActive) { StartMetadataIndexing(false); SaveCatalogState(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Проверка каталога: {ex.Message}"; }
    }

    private void SaveCatalogState()
    {
        if (_fileOperationActive || _isInitializing || !_catalogLoaded || _isCatalogLoading) return;
        _saveCancellation?.Cancel();
        _saveCancellation = new CancellationTokenSource();
        _pendingSave = SaveAfterDelayAsync(_saveCancellation.Token);
    }

    private async Task FindExactDuplicatesAsync(DuplicateSearchScope scope)
    {
        if (_duplicateCancellation is not null || _fileOperationActive) return;
        if (_isCatalogLoading || !_catalogLoaded)
        {
            System.Windows.MessageBox.Show("Дождитесь загрузки каталога перед поиском дублей.", "PhotoShelf"); return;
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _duplicateCancellation = operation;
        var token = operation.Token;
        var snapshot = Photos.ToArray();
        var visible = _visiblePhotos;
        var cache = new Dictionary<string, SavedDuplicateHash>(_duplicateHashCache, StringComparer.OrdinalIgnoreCase);
        var excluded = _excludedFolders.ToArray();
        var folder = _activeFolder;
        var compareA = _duplicateCompareFolderA;
        var compareB = _duplicateCompareFolderB;
        var videos = ShowVideos;
        SetDuplicateSearch(true, 1);
        DuplicateProgressBar.IsIndeterminate = true;
        StatusText.Text = "Подготавливаю поиск дублей…";
        try
        {
            var result = await Task.Run(async () =>
            {
                IEnumerable<PhotoItem> query = scope == DuplicateSearchScope.CurrentView ? visible : snapshot;
                query = query.Where(x => videos || !x.IsVideo);
                query = scope switch
                {
                    DuplicateSearchScope.IncludedFolders => query.Where(x => !excluded.Any(p => IsUnderFolder(x.Path, p))),
                    DuplicateSearchScope.CurrentFolder when folder is not null => query.Where(x => IsUnderFolder(x.Path, folder)),
                    DuplicateSearchScope.CompareTwoFolders when compareA is not null && compareB is not null => query.Where(x => IsUnderFolder(x.Path, compareA) || IsUnderFolder(x.Path, compareB)),
                    _ => query
                };
                var fresh = new List<PhotoItem>();
                foreach (var item in query)
                {
                    token.ThrowIfCancellationRequested();
                    try { var file = new FileInfo(item.Path); if (file.Exists && file.Length > 0) fresh.Add(new PhotoItem(item.Path, file.Length, file.LastWriteTime)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                var candidates = fresh.GroupBy(x => x.FileSizeBytes).Where(g => g.Count() > 1).SelectMany(g => g).ToArray();
                var byHash = new Dictionary<(long Size, string Hash), List<PhotoItem>>();
                var newHashes = new List<SavedDuplicateHash>();
                var count = 0;
                foreach (var item in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    string? hash = null;
                    if (cache.TryGetValue(item.Path, out var saved) && saved.SizeBytes == item.FileSizeBytes && saved.FileModifiedAt == item.FileModifiedAt) hash = saved.Hash;
                    if (string.IsNullOrEmpty(hash))
                    {
                        try
                        {
                            await using var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                            var after = new FileInfo(item.Path);
                            if (after.Length != item.FileSizeBytes || after.LastWriteTime != item.FileModifiedAt) continue;
                            newHashes.Add(new SavedDuplicateHash { Path = item.Path, SizeBytes = item.FileSizeBytes, FileModifiedAt = item.FileModifiedAt, Hash = hash });
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    }
                    if (!byHash.TryGetValue((item.FileSizeBytes, hash), out var group)) byHash[(item.FileSizeBytes, hash)] = group = new();
                    group.Add(item);
                    if (++count % 25 == 0)
                        await Dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) StatusText.Text = $"Дубли: {count} / {candidates.Length}"; }, DispatcherPriority.Background, token);
                }
                var groups = byHash.Where(x => x.Value.Count > 1)
                    .Where(x => scope != DuplicateSearchScope.CompareTwoFolders || (compareA is not null && compareB is not null && x.Value.Any(i => IsUnderFolder(i.Path, compareA)) && x.Value.Any(i => IsUnderFolder(i.Path, compareB))))
                    .Select(x => new DuplicateGroup(x.Key.Size, x.Key.Hash, x.Value)).ToArray();
                await _duplicateHashStore.SaveAsync(newHashes, token);
                return (groups, newHashes);
            }, token);
            token.ThrowIfCancellationRequested();
            foreach (var hash in result.newHashes) _duplicateHashCache[hash.Path] = hash;
            SaveCatalogState();
            if (result.groups.Length == 0) System.Windows.MessageBox.Show("Точных дублей не найдено.", "Дубликаты");
            else
            {
                var models = await Task.Run(() => new ObservableCollection<DuplicateGroupViewModel>(result.groups.Select((group, index) => new DuplicateGroupViewModel(group, index + 1))), token);
                var review = new DuplicateReviewWindow(models, async entry =>
                {
                    await _saveGate.WaitAsync();
                    try { await _desktopCatalogStore.MoveItemAsync(entry.Source, entry.Destination, removeFromLibrary: true); }
                    finally { _saveGate.Release(); }
                    await Dispatcher.InvokeAsync(() => ApplyMovedItem(entry.Source, null));
                }) { Owner = this };
                _fileOperationActive = true;
                _scanCancellation?.Cancel();
                _browseCancellation?.Cancel();
                _metadataIndexCancellation?.Cancel();
                _saveCancellation?.Cancel();
                try
                {
                    await _pendingSave;
                    await PersistStateAsync(CaptureState(), CancellationToken.None, _catalogRevision);
                    review.ShowDialog();
                }
                finally { _fileOperationActive = false; SaveCatalogState(); RebuildRows(); }
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "Поиск дублей отменён"; }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Поиск дублей"); }
        finally
        {
            if (ReferenceEquals(_duplicateCancellation, operation)) _duplicateCancellation = null;
            SetDuplicateSearch(false, 0);
        }
    }

    private async Task<DuplicateSearchScope?> AskDuplicateScopeAsync()
    {
        var snapshot = Photos.ToArray(); var visibleCount = _visiblePhotos.Length;
        var excluded = _excludedFolders.ToArray(); var videos = ShowVideos;
        var counts = await Task.Run(() => (All: snapshot.Count(x => videos || !x.IsVideo),
            Included: snapshot.Count(x => (videos || !x.IsVideo) && !excluded.Any(p => IsUnderFolder(x.Path, p)))));
        var dialog = new DuplicateSearchDialog(visibleCount, counts.Included, counts.All,
            _viewMode == LibraryViewMode.Folder ? _activeFolder : null) { Owner = this };
        if (dialog.ShowDialog() != true) return null;
        _duplicateCompareFolderA = dialog.CompareFolderA;
        _duplicateCompareFolderB = dialog.CompareFolderB;
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
        if (InfoPanel.Visibility != Visibility.Visible) return;
        var item = _selectedPhoto;
        var revision = ++_infoRevision;
        InfoText.Text = item is null ? "Выберите фото или видео." : "Читаю сведения…";
        if (item is null) return;
        var text = await Task.Run(() => item.MetadataText);
        if (revision == _infoRevision && ReferenceEquals(item, _selectedPhoto)) InfoText.Text = text;
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
