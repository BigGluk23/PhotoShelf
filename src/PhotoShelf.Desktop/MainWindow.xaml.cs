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
    private const string VersionLabel = "v0.9.6";
    private readonly Dictionary<string, FolderNode> _folderNodes = new(StringComparer.OrdinalIgnoreCase);
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
    private bool _isUpdatingFolderChecks;
    private readonly DispatcherTimer _folderFilterRefreshTimer;
    private bool _sortNewestFirst = true;
    private DateGroupingMode _dateGroupingMode = DateGroupingMode.FileDate;
    private bool _showOnlyMissingCaptureDate;
    private PhotoItem? _selectedPhoto;
    private string? _duplicateCompareFolderA;
    private string? _duplicateCompareFolderB;
    private LibraryViewMode _viewMode = LibraryViewMode.All;
    private readonly HashSet<string> _collapsedDateGroups = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        Title = $"PhotoShelf {VersionLabel}";
        AppTitleText.Text = $"PhotoShelf {VersionLabel}";
        DataContext = this;
        _catalogState = LoadInitialCatalogState();
        foreach (var hash in LoadInitialDuplicateHashes(_catalogState))
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
        _dateGroupingMode = DateGroupingMode.FileDate;
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
        _ = InitializeSqliteCatalogAsync();
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

        await ScanRootsAsync(PhotoScanner.GetDefaultDiscoveryRoots());
    }

    private async void OnDuplicatesClicked(object sender, RoutedEventArgs e)
    {
        var scope = AskDuplicateScope();
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
        RebuildRows();
        RefreshChrome();
        SaveCatalogState();
    }

    private async void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeMetadataIndexAsync();
        _catalogLoaded = true;
        _isInitializing = false;
        RefreshChrome();
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
        _activeFolder = null;
        _viewMode = LibraryViewMode.All;
        FolderTree.Focus();
        ViewTitleText.Text = "Все фотографии";
        RebuildRows();
        RefreshChrome();
    }

    private void OnFavoritesClicked(object sender, RoutedEventArgs e)
    {
        _activeFolder = null;
        _viewMode = LibraryViewMode.Favorites;
        FolderTree.Focus();
        ViewTitleText.Text = "Избранное";
        RebuildRows();
        RefreshChrome();
    }

    private void OnRecentClicked(object sender, RoutedEventArgs e)
    {
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

        StartBackgroundCatalogMaintenance();
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
        if (_isUpdatingFolderChecks)
        {
            e.Handled = true;
            return;
        }

        if (!_isUpdatingFolderChecks && sender is System.Windows.Controls.CheckBox { DataContext: FolderNode node })
        {
            _ = SetFolderIncludedRecursiveAsync(node, node.IsIncluded);
        }

        QueueFolderFilterRefresh();
        e.Handled = true;
    }

    private async Task SetFolderIncludedRecursiveAsync(FolderNode node, bool isIncluded)
    {
        _isUpdatingFolderChecks = true;
        try
        {
            var pending = new Queue<FolderNode>(node.Children);
            var changed = 0;
            while (pending.Count > 0)
            {
                var child = pending.Dequeue();
                child.IsIncluded = isIncluded;
                foreach (var grandChild in child.Children)
                {
                    pending.Enqueue(grandChild);
                }

                changed++;
                if (changed % 100 == 0)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }
        }
        finally
        {
            _isUpdatingFolderChecks = false;
            QueueFolderFilterRefresh();
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

        SelectPhoto(item);

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
        var visibleItems = GetVisiblePhotos().ToArray();
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
        if (sender is not FrameworkElement { DataContext: FolderNode node } || !Directory.Exists(node.FullPath))
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
        if (sender is not FrameworkElement { DataContext: FolderNode node } || !Directory.Exists(node.FullPath))
        {
            return;
        }

        await ScanRootsAsync(new[] { node.FullPath });
    }

    private async void OnFolderContextDuplicatesClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderNode node } || !Directory.Exists(node.FullPath))
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

    private async Task ScanRootsAsync(IEnumerable<string> roots)
    {
        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();

        SetScanning(true);
        var token = _scanCancellation.Token;
        var rootList = roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var root in rootList)
        {
            AddFolder(root);
        }

        try
        {
            _activeFolder = null;
            _viewMode = LibraryViewMode.All;
            RebuildRows();
            ViewTitleText.Text = "Все фотографии";
            var progress = new Progress<PhotoScanBatch>(ApplyScanBatch);
            await PhotoScanner.ScanAsync(rootList, progress, _includeSystemFolders, token);
            RebuildRows();
            StatusText.Text = FormatPhotoCount(Photos.Count);
            SaveCatalogState();
            StartMetadataIndexing(resetExisting: false);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Поиск остановлен. Найдено: {Photos.Count}";
        }
        finally
        {
            SetScanning(false);
            RefreshChrome();
        }
    }

    private void ApplyScanBatch(PhotoScanBatch batch)
    {
        if (batch.Paths.Count == 0)
        {
            StatusText.Text = $"Сканирую: {batch.CurrentFolder}";
            return;
        }

        foreach (var path in batch.Paths)
        {
            if (_knownPhotoPaths.Add(path))
            {
                var item = new PhotoItem(path);
                Photos.Add(item);
                AddFolder(item.Folder, incrementDirectCount: true);
                AddPhotoToRows(item);
            }
        }

        EmptyState.Visibility = Photos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = $"{FormatPhotoCount(Photos.Count)} найдено";
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
        if (_viewMode == LibraryViewMode.Favorites && !_selectedPhoto.IsFavorite)
        {
            RebuildRows();
        }

        SaveCatalogState();
    }

    private void OnInfoClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedPhoto is null)
        {
            return;
        }

        System.Windows.MessageBox.Show(
            $"{_selectedPhoto.FileName}\n\nТип: {_selectedPhoto.MediaTypeLabel}\nРазмер: {_selectedPhoto.FileSizeBytes / 1024d / 1024d:0.0} MB\nДата файла: {_selectedPhoto.FileModifiedAt:dd.MM.yyyy HH:mm}\nПапка: {_selectedPhoto.Folder}\n\nПуть:\n{_selectedPhoto.Path}",
            "Сведения",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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
        SaveCatalogState();
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
        rootNode.IsExpanded = true;

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

        node = new FolderNode(normalized);
        _folderNodes.Add(normalized, node);

        var collection = parent?.Children ?? FolderRoots;
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
        if (!Directory.Exists(parent.FullPath))
        {
            return;
        }

        string[] directories;
        try
        {
            var includeSystemFolders = _includeSystemFolders;
            directories = await Task.Run(() => Directory
                .EnumerateDirectories(parent.FullPath)
                .Where(directory => !ShouldHideFolderInTree(directory, includeSystemFolders))
                .ToArray());
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        foreach (var directory in directories)
        {
            EnsureFolderNode(directory, parent);
        }
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

    private void RebuildFolderTreeFromPhotos()
    {
        var excludedFolders = _folderNodes.Values
            .Where(static node => !node.IsIncluded)
            .Select(static node => node.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expandedFolders = _folderNodes.Values
            .Where(static node => node.IsExpanded)
            .Select(static node => node.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _folderNodes.Clear();
        FolderRoots.Clear();
        foreach (var item in Photos)
        {
            AddFolder(item.Folder, incrementDirectCount: true);
        }

        foreach (var excludedFolder in excludedFolders)
        {
            if (_folderNodes.TryGetValue(NormalizePath(excludedFolder), out var node))
            {
                node.IsIncluded = false;
            }
        }

        foreach (var expandedFolder in expandedFolders)
        {
            if (_folderNodes.TryGetValue(NormalizePath(expandedFolder), out var node))
            {
                node.IsExpanded = true;
            }
        }
    }

    private void RefreshChrome()
    {
        EmptyState.Visibility = Photos.Count == 0 || PhotoRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = FormatPhotoCount(Photos.Count);
        OnPropertyChanged(nameof(Photos));
        OnPropertyChanged(nameof(PhotoRows));
        OnPropertyChanged(nameof(FolderRoots));
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private IEnumerable<PhotoItem> GetVisiblePhotos()
    {
        var query = Photos.Where(IsVisibleByType);
        if (_viewMode != LibraryViewMode.Folder)
        {
            query = query.Where(photo => IsFolderIncluded(photo.Folder));
        }

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            query = query.Where(photo =>
                photo.FileName.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase) ||
                photo.Folder.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase));
        }

        if (_showOnlyMissingCaptureDate)
        {
            query = query.Where(static photo => !photo.IsVideo && photo.CaptureDate is null);
        }

        query = _viewMode switch
        {
            LibraryViewMode.Folder when !string.IsNullOrWhiteSpace(_activeFolder) =>
                query.Where(photo => IsUnderFolder(photo.Path, _activeFolder)),
            LibraryViewMode.Favorites => query.Where(static photo => photo.IsFavorite),
            LibraryViewMode.Recent => query.OrderByDescending(static photo => photo.FileModifiedAt ?? DateTime.MinValue).Take(500),
            _ => query
        };

        return _sortNewestFirst
            ? query.OrderByDescending(GetSortDateForCurrentMode)
            : query.OrderBy(GetSortDateForCurrentMode);
    }

    private bool IsFolderIncluded(string folder)
    {
        var current = NormalizePath(folder);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (_folderNodes.TryGetValue(current, out var node) && !node.IsIncluded)
            {
                return false;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = NormalizePath(parent.FullName);
        }

        return true;
    }

    private bool IsVisibleByType(PhotoItem item)
    {
        return ShowVideos || !item.IsVideo;
    }

    private void RebuildRows()
    {
        PhotoRows.Clear();
        var visiblePhotos = GetVisiblePhotos().ToArray();
        foreach (var group in visiblePhotos.GroupBy(GetYearMonthKey))
        {
            var groupKey = FormatYearMonthKey(group.Key);
            var isCollapsed = _collapsedDateGroups.Contains(groupKey);
            PhotoRows.Add(PhotoRow.CreateHeader(FormatYearMonthHeader(group.Key), groupKey, isCollapsed));
            if (isCollapsed)
            {
                continue;
            }

            foreach (var chunk in group.Chunk(_columns))
            {
                PhotoRows.Add(new PhotoRow(chunk));
            }
        }
    }

    private (int Year, int Month, bool HasDate) GetYearMonthKey(PhotoItem item)
    {
        var date = GetDisplayDate(item);
        return date is null ? (0, 0, false) : (date.Value.Year, date.Value.Month, true);
    }

    private string FormatYearMonthHeader((int Year, int Month, bool HasDate) key)
    {
        if (!key.HasDate || key.Year <= 1 || key.Month is < 1 or > 12)
        {
            return _dateGroupingMode == DateGroupingMode.CaptureDate ? "Без даты съёмки" : "Без даты файла";
        }

        var month = CultureInfo.GetCultureInfo("ru-RU").DateTimeFormat.GetMonthName(key.Month);
        month = CultureInfo.GetCultureInfo("ru-RU").TextInfo.ToTitleCase(month);
        return $"{key.Year} / {month}";
    }

    private string FormatYearMonthKey((int Year, int Month, bool HasDate) key)
    {
        var mode = _dateGroupingMode == DateGroupingMode.CaptureDate ? "capture" : "file";
        return key.HasDate ? $"{mode}:{key.Year:D4}-{key.Month:D2}" : $"{mode}:none";
    }

    private DateTime? GetDisplayDate(PhotoItem item)
    {
        return _dateGroupingMode == DateGroupingMode.CaptureDate ? item.CaptureDate : item.FileModifiedAt;
    }

    private DateTime GetSortDateForCurrentMode(PhotoItem item)
    {
        return GetDisplayDate(item) ?? DateTime.MinValue;
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

    private void AddPhotoToRows(PhotoItem item)
    {
        if (!IsVisibleByType(item))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_activeFolder) &&
            !IsUnderFolder(item.Path, _activeFolder))
        {
            return;
        }

        if (PhotoRows.Count == 0 || PhotoRows[^1].IsHeader || PhotoRows[^1].Items.Count >= _columns)
        {
            PhotoRows.Add(new PhotoRow(new[] { item }));
            return;
        }

        PhotoRows[^1].Items.Add(item);
    }

    private static async Task InitializeSqliteCatalogAsync()
    {
        try
        {
            var databasePath = Path.Combine(LocalCatalogStore.CatalogDirectory, "catalog-v2.sqlite");
            var database = new SqliteCatalogDatabase(new SqliteCatalogOptions(databasePath));
            await database.InitializeAsync().ConfigureAwait(false);
        }
        catch
        {
            // JSON-каталог остаётся безопасным fallback'ом; SQLite не должен ломать запуск UI.
        }
    }

    private async Task InitializeMetadataIndexAsync()
    {
        try
        {
            await _desktopCatalogStore.InitializeAsync();
            await _metadataIndexStore.InitializeAsync();
            await _duplicateHashStore.InitializeAsync();
        }
        catch
        {
            MetadataStatusText.Text = "Метаданные: база недоступна";
        }
    }

    private LocalCatalogState LoadInitialCatalogState()
    {
        try
        {
            _desktopCatalogStore.InitializeAsync().GetAwaiter().GetResult();
            var sqliteState = _desktopCatalogStore.LoadAsync().GetAwaiter().GetResult();
            if (sqliteState.Items.Count > 0)
            {
                var jsonState = LocalCatalogStore.Load();
                sqliteState.DuplicateHashes = jsonState.DuplicateHashes;
                return sqliteState;
            }
        }
        catch
        {
            // JSON остаётся fallback'ом, чтобы не потерять рабочий каталог.
        }

        return LocalCatalogStore.Load();
    }

    private IReadOnlyList<SavedDuplicateHash> LoadInitialDuplicateHashes(LocalCatalogState state)
    {
        try
        {
            _duplicateHashStore.InitializeAsync().GetAwaiter().GetResult();
            var sqliteHashes = _duplicateHashStore.LoadAsync().GetAwaiter().GetResult();
            if (sqliteHashes.Count > 0)
            {
                return sqliteHashes;
            }
        }
        catch
        {
        }

        return state.DuplicateHashes;
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
        _ = Task.Run(async () =>
        {
            var indexed = 0;
            var changedSinceUiRefresh = 0;
            foreach (var item in items)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                var changed = item.TryLoadCaptureDate();
                try
                {
                    await _metadataIndexStore.SaveAsync(item, token).ConfigureAwait(false);
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

                if (indexed % 50 == 0 || indexed == items.Length)
                {
                    var current = indexed;
                    var shouldRebuild = _dateGroupingMode == DateGroupingMode.CaptureDate && changedSinceUiRefresh > 0;
                    changedSinceUiRefresh = 0;
                    await Dispatcher.BeginInvoke(() =>
                    {
                        MetadataStatusText.Text = $"Метаданные: {current} / {items.Length}";
                        if (shouldRebuild)
                        {
                            RebuildRows();
                            RefreshChrome();
                        }

                        UpdateInfoPanel();
                    });
                }
            }

            if (!token.IsCancellationRequested)
            {
                await Dispatcher.BeginInvoke(() =>
                {
                    MetadataStatusText.Text = "Метаданные: готово";
                    if (_dateGroupingMode == DateGroupingMode.CaptureDate)
                    {
                        RebuildRows();
                        RefreshChrome();
                    }
                });
            }
        }, token);
    }

    private async Task LoadSavedCatalogAsync()
    {
        if (_catalogState.Items.Count == 0)
        {
            return;
        }

        StatusText.Text = $"Загружаю каталог: 0 / {_catalogState.Items.Count}";
        Dictionary<string, DateTime?> indexedCaptureDates;
        try
        {
            indexedCaptureDates = await _metadataIndexStore.LoadCaptureDatesAsync();
        }
        catch
        {
            indexedCaptureDates = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
        }

        var loadedSinceUiRefresh = 0;
        var progress = new Progress<IReadOnlyList<PhotoItem>>(batch =>
        {
            foreach (var item in batch)
            {
                if (!_knownPhotoPaths.Add(item.Path))
                {
                    continue;
                }

                Photos.Add(item);
                loadedSinceUiRefresh++;
            }

            if (loadedSinceUiRefresh >= 5000)
            {
                loadedSinceUiRefresh = 0;
                RebuildFolderTreeFromPhotos();
                RebuildRows();
                RefreshChrome();
            }

            StatusText.Text = $"Загружаю каталог: {Photos.Count} / {_catalogState.Items.Count}";
        });

        await Task.Run(() =>
        {
            var batch = new List<PhotoItem>(250);
            foreach (var savedItem in _catalogState.Items)
            {
                if (string.IsNullOrWhiteSpace(savedItem.Path) ||
                    PhotoScanner.IsIgnoredPath(savedItem.Path, _includeSystemFolders) ||
                    !File.Exists(savedItem.Path) ||
                    !PhotoItem.IsSupported(savedItem.Path))
                {
                    continue;
                }

                var item = new PhotoItem(savedItem.Path)
                {
                    IsFavorite = savedItem.IsFavorite
                };
                if (indexedCaptureDates.TryGetValue(savedItem.Path, out var captureDate))
                {
                    item.ApplyIndexedCaptureDate(captureDate);
                }

                batch.Add(item);

                if (batch.Count < 250)
                {
                    continue;
                }

                ((IProgress<IReadOnlyList<PhotoItem>>)progress).Report(batch.ToArray());
                batch.Clear();
            }

            if (batch.Count > 0)
            {
                ((IProgress<IReadOnlyList<PhotoItem>>)progress).Report(batch.ToArray());
            }
        });

        RebuildFolderTreeFromPhotos();
        foreach (var excludedFolder in _catalogState.ExcludedFolders)
        {
            var normalized = NormalizePath(excludedFolder);
            if (_folderNodes.TryGetValue(normalized, out var node))
            {
                node.IsIncluded = false;
            }
        }

        RebuildRows();
        StatusText.Text = FormatPhotoCount(Photos.Count);
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
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                var before = Photos.Count;
                RemoveMissingCatalogItems();
                var removed = before - Photos.Count;
                if (removed > 0)
                {
                    StatusText.Text = $"Каталог очищен в фоне: убрано {removed}";
                }
            });
        });
    }

    private void SaveCatalogState()
    {
        if (_isCatalogLoading)
        {
            return;
        }

        if (!_catalogLoaded && _catalogState.Items.Count > 0)
        {
            return;
        }

        try
        {
            _catalogState.TileWidth = TileWidth;
            _catalogState.ShowVideos = ShowVideos;
            _catalogState.IncludeSystemFolders = _includeSystemFolders;
            _catalogState.DateGroupingMode = _dateGroupingMode.ToString();
            _catalogState.Items = Photos
                .Select(static item => new SavedMediaItem
                {
                    Path = item.Path,
                    IsFavorite = item.IsFavorite
                })
                .ToList();
            _catalogState.ExcludedFolders = _folderNodes.Values
                .Where(static node => !node.IsIncluded)
                .Select(static node => node.FullPath)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _catalogState.DuplicateHashes = _duplicateHashCache.Values
                .Where(hash => _knownPhotoPaths.Contains(hash.Path))
                .OrderBy(static hash => hash.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            LocalCatalogStore.Save(_catalogState);
            _ = _desktopCatalogStore.SaveAsync(_catalogState);
        }
        catch
        {
            StatusText.Text = "Не удалось сохранить каталог";
        }
    }

    private async Task FindExactDuplicatesAsync(DuplicateSearchScope scope)
    {
        if (_duplicateCancellation is not null)
        {
            return;
        }

        var duplicateScope = GetDuplicateScope(scope).ToArray();
        var candidates = duplicateScope
            .Where(static item => item.FileSizeBytes > 0)
            .GroupBy(static item => item.FileSizeBytes)
            .Where(static group => group.Count() > 1)
            .SelectMany(static group => group)
            .ToArray();

        if (candidates.Length == 0)
        {
            System.Windows.MessageBox.Show("Точных дублей пока не найдено.", "Дубликаты", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _duplicateCancellation = new CancellationTokenSource();
        var token = _duplicateCancellation.Token;
        var itemsNeedingHash = candidates.Count(item => !TryGetCachedHash(item, out _));
        SetDuplicateSearch(true, Math.Max(1, itemsNeedingHash));
        StatusText.Text = $"Дубли: проверяю {FormatDuplicateScopeLabel(scope)} ({duplicateScope.Length})";
        var progress = new Progress<DuplicateSearchProgress>(state =>
        {
            DuplicateProgressBar.Value = state.Checked;
            StatusText.Text = state.Total == 0
                ? "Дубли: использую сохранённые данные"
                : $"Дубли: {state.Checked} / {state.Total}";
        });

        DuplicateGroup[] duplicates;
        List<SavedDuplicateHash> newHashes;
        try
        {
            var result = await Task.Run(() =>
            {
                var byHash = new Dictionary<(long Size, string Hash), List<PhotoItem>>();
                var checkedCount = 0;
                var newHashList = new List<SavedDuplicateHash>();
                foreach (var item in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (!TryGetCachedHash(item, out var hash))
                    {
                        try
                        {
                            using var stream = File.OpenRead(item.Path);
                            hash = Convert.ToHexString(SHA256.HashData(stream));
                            newHashList.Add(new SavedDuplicateHash
                            {
                                Path = item.Path,
                                SizeBytes = item.FileSizeBytes,
                                FileModifiedAt = item.FileModifiedAt,
                                Hash = hash
                            });
                        }
                        catch
                        {
                            hash = null;
                        }

                        checkedCount++;
                        if (checkedCount == 1 || checkedCount % 25 == 0 || checkedCount == itemsNeedingHash)
                        {
                            ((IProgress<DuplicateSearchProgress>)progress).Report(new DuplicateSearchProgress(checkedCount, itemsNeedingHash));
                        }
                    }

                    if (string.IsNullOrWhiteSpace(hash))
                    {
                        continue;
                    }

                    var key = (item.FileSizeBytes, hash);
                    if (!byHash.TryGetValue(key, out var list))
                    {
                        list = new List<PhotoItem>();
                        byHash.Add(key, list);
                    }

                    list.Add(item);
                }

                var groups = byHash
                    .Where(pair => IsDuplicateGroupInScope(pair.Value, scope))
                    .Select(static pair => new DuplicateGroup(pair.Key.Size, pair.Key.Hash, pair.Value))
                    .OrderByDescending(static group => group.Items.Count)
                    .ToArray();

                return (Groups: groups, NewHashes: newHashList);
            }, token);
            duplicates = result.Groups;
            newHashes = result.NewHashes;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Проверка дублей остановлена";
            return;
        }
        finally
        {
            _duplicateCancellation.Dispose();
            _duplicateCancellation = null;
            SetDuplicateSearch(false, 0);
        }

        foreach (var hash in newHashes)
        {
            _duplicateHashCache[hash.Path] = hash;
        }

        if (newHashes.Count > 0)
        {
            try
            {
                await _duplicateHashStore.SaveAsync(newHashes, CancellationToken.None);
            }
            catch
            {
            }
        }

        SaveCatalogState();
        StatusText.Text = FormatPhotoCount(Photos.Count);
        if (duplicates.Length == 0)
        {
            System.Windows.MessageBox.Show("Точных дублей пока не найдено.", "Дубликаты", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var reviewWindow = new DuplicateReviewWindow(duplicates)
            {
                Owner = this
            };
            reviewWindow.ShowDialog();
            RemoveMissingCatalogItems();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Не удалось открыть окно дублей.\n\n{ex.Message}",
                "Дубликаты",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private DuplicateSearchScope? AskDuplicateScope()
    {
        var dialog = new DuplicateSearchDialog(
            currentViewCount: GetVisiblePhotos().Count(),
            includedFoldersCount: Photos.Where(photo => IsVisibleByType(photo) && IsFolderIncluded(photo.Folder)).Count(),
            wholeLibraryCount: Photos.Where(IsVisibleByType).Count(),
            currentFolder: _viewMode == LibraryViewMode.Folder ? _activeFolder : null)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        _duplicateCompareFolderA = dialog.CompareFolderA;
        _duplicateCompareFolderB = dialog.CompareFolderB;
        return dialog.SelectedScope;
    }

    private IEnumerable<PhotoItem> GetDuplicateScope(DuplicateSearchScope scope)
    {
        return scope switch
        {
            DuplicateSearchScope.WholeLibrary => Photos.Where(IsVisibleByType),
            DuplicateSearchScope.IncludedFolders => Photos.Where(photo => IsVisibleByType(photo) && IsFolderIncluded(photo.Folder)),
            DuplicateSearchScope.CurrentFolder when !string.IsNullOrWhiteSpace(_activeFolder) =>
                Photos.Where(photo => IsVisibleByType(photo) && IsUnderFolder(photo.Path, _activeFolder)),
            DuplicateSearchScope.CompareTwoFolders when !string.IsNullOrWhiteSpace(_duplicateCompareFolderA) && !string.IsNullOrWhiteSpace(_duplicateCompareFolderB) =>
                Photos.Where(photo => IsVisibleByType(photo) &&
                    (IsUnderFolder(photo.Path, _duplicateCompareFolderA) ||
                     IsUnderFolder(photo.Path, _duplicateCompareFolderB))),
            _ => GetVisiblePhotos()
        };
    }

    private bool IsDuplicateGroupInScope(IReadOnlyCollection<PhotoItem> items, DuplicateSearchScope scope)
    {
        if (items.Count < 2)
        {
            return false;
        }

        if (scope != DuplicateSearchScope.CompareTwoFolders ||
            string.IsNullOrWhiteSpace(_duplicateCompareFolderA) ||
            string.IsNullOrWhiteSpace(_duplicateCompareFolderB))
        {
            return true;
        }

        var hasA = items.Any(item => IsUnderFolder(item.Path, _duplicateCompareFolderA));
        var hasB = items.Any(item => IsUnderFolder(item.Path, _duplicateCompareFolderB));
        return hasA && hasB;
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

    private static string FormatDuplicateScopeLabel(DuplicateSearchScope scope)
    {
        return scope switch
        {
            DuplicateSearchScope.WholeLibrary => "всю библиотеку",
            DuplicateSearchScope.IncludedFolders => "отмеченные папки",
            DuplicateSearchScope.CurrentFolder => "текущую папку",
            DuplicateSearchScope.CompareTwoFolders => "две папки",
            _ => "текущий вид"
        };
    }

    private bool TryGetCachedHash(PhotoItem item, out string? hash)
    {
        hash = null;
        if (!_duplicateHashCache.TryGetValue(item.Path, out var cached))
        {
            return false;
        }

        if (cached.SizeBytes != item.FileSizeBytes ||
            cached.FileModifiedAt != item.FileModifiedAt ||
            string.IsNullOrWhiteSpace(cached.Hash))
        {
            return false;
        }

        hash = cached.Hash;
        return true;
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

    private sealed record DuplicateSearchProgress(int Checked, int Total);

    private void RemoveMissingCatalogItems()
    {
        var removedAny = false;
        for (var index = Photos.Count - 1; index >= 0; index--)
        {
            if (File.Exists(Photos[index].Path) && !PhotoScanner.IsIgnoredPath(Photos[index].Path, _includeSystemFolders))
            {
                continue;
            }

            _knownPhotoPaths.Remove(Photos[index].Path);
            Photos.RemoveAt(index);
            removedAny = true;
        }

        if (!removedAny)
        {
            return;
        }

        var excludedFolders = _folderNodes.Values
            .Where(static node => !node.IsIncluded)
            .Select(static node => node.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _folderNodes.Clear();
        FolderRoots.Clear();
        foreach (var item in Photos)
        {
            AddFolder(item.Folder, incrementDirectCount: true);
        }

        foreach (var excludedFolder in excludedFolders)
        {
            if (_folderNodes.TryGetValue(NormalizePath(excludedFolder), out var node))
            {
                node.IsIncluded = false;
            }
        }

        RebuildRows();
        RefreshChrome();
        SaveCatalogState();
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

    private void UpdateInfoPanel()
    {
        if (InfoPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        InfoText.Text = _selectedPhoto?.MetadataText ?? "Выберите фото или видео.";
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
