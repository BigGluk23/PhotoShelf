using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Binding = System.Windows.Data.Binding;
using CheckBox = System.Windows.Controls.CheckBox;

namespace PhotoShelf.Desktop;

// Only the diagnostic process calls these seams. Selection and checkbox actions
// still execute production handlers; catalog isolation is never disabled.
public partial class MainWindow
{
    private string? _browseSmokeRoot;

    internal void PrepareBrowseSmokeTree(string root)
    {
        if (!LocalCatalogStore.IsIsolatedSmokeCatalog || !AllowIsolatedLibraryMonitoring)
            throw new InvalidOperationException("Browse smoke requires isolated catalog and explicit monitoring opt-in.");
        root = Path.GetFullPath(root);
        if (!Path.GetFileName(root).StartsWith("PhotoShelf-browse-smoke-", StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(root), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Browse smoke tree must belong to its fresh temporary fixture.");
        _browseSmokeRoot = root;
        // No real drive ancestors are expanded to reach the fixture. Normal startup
        // may add drive labels later, but the scenario only selects owned nodes.
        _folderNodes.Clear(); FolderRoots.Clear();
        EnsureFolderNode(root, null);
    }

    private FolderNode BrowseSmokeNode(string path)
    {
        path = Path.GetFullPath(path);
        if (_browseSmokeRoot is null || !LibraryCatalogSynchronizer.IsUnder(path, _browseSmokeRoot))
            throw new InvalidOperationException("Browse smoke cannot select a path outside its owned fixture.");
        var node = EnsureFolderNode(_browseSmokeRoot, null);
        var relative = Path.GetRelativePath(_browseSmokeRoot, path);
        if (relative == ".") return node;
        var current = _browseSmokeRoot;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            node = EnsureFolderNode(current, node);
        }
        return node;
    }

    internal void BrowseSmokeSelectFolder(string path)
    {
        var node = BrowseSmokeNode(path);
        // Same event handler as TreeView.SelectedItemChanged. Expanding real C:\
        // ancestors solely to realize a test container would violate isolation.
        OnFolderTreeSelectedItemChanged(FolderTree,
            new RoutedPropertyChangedEventArgs<object>(null!, node));
    }

    internal bool? BrowseSmokeClickInclusion(string path)
    {
        var node = BrowseSmokeNode(path);
        var checkbox = new BrowseSmokeCheckBox { DataContext = node, IsThreeState = true };
        checkbox.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(FolderNode.CheckState)) { Mode = BindingMode.OneWay });
        checkbox.Click += OnFolderIncludedChanged;
        checkbox.ClickForSmoke();
        if (checkbox.IsChecked != node.CheckState)
            throw new InvalidOperationException("Folder checkbox did not update synchronously.");
        return node.CheckState;
    }

    internal void BrowseSmokeSetSubfolders(bool value)
    {
        SubfoldersCheckBox.IsChecked = value;
        SubfoldersCheckBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, SubfoldersCheckBox));
    }
    internal bool BrowseSmokeRootIsMixed => _browseSmokeRoot is not null && BrowseSmokeNode(_browseSmokeRoot).CheckState is null;
    internal FrameworkElement BrowseSmokeEmptyState => EmptyState;
    internal string? BrowseSmokePublishedFolder => _currentQuery?.Folder;
    internal long BrowseSmokeItemCount => (PhotoRows as VirtualPhotoRows)?.ItemCount ?? -1;
    internal bool BrowseSmokeRecursive => _currentQuery?.IncludeSubfolders == true;
    internal bool BrowseSmokeMonitorReady => _libraryMonitor is not null && _libraryRootStates.Count > 0 && _monitorConfigurationTask.IsCompletedSuccessfully;
    internal long BrowseSmokeMonitorGeneration => _libraryMonitor?.Generation ?? -1;
    internal bool BrowseSmokeBusy => _isProjecting || _projectionQueued || LibraryMonitorCallbackActive || !_browseTask.IsCompleted || !_scanTask.IsCompleted ||
        !_metadataTask.IsCompleted || !_fingerprintTask.IsCompleted || !_monitorConfigurationTask.IsCompleted || _libraryMonitor?.Activity is { } activity &&
        (activity.IsProcessing || activity.PendingPathCount > 0 || activity.PendingDirectoryCount > 0 || activity.PendingReconciliationRootCount > 0);
    internal bool BrowseSmokeReadersDrained => _scanTask.IsCompleted && _browseTask.IsCompleted && _metadataTask.IsCompleted &&
        _fingerprintTask.IsCompleted && !LibraryMonitorCallbackActive && _libraryMonitor?.Activity.IsProcessing != true;
    internal bool BrowseSmokeWritersDrained => _closeReady && BrowseSmokeReadersDrained;
    internal string[] BrowseSmokeLoadedPaths => (PhotoRows as VirtualPhotoRows)?.LoadedItems.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];

    internal string? BrowseSmokePublishedSearch => _currentQuery?.SearchText;
    internal bool BrowseSmokeNewestFirst => _currentQuery?.NewestFirst == true;
    internal bool BrowseSmokeMonitoringEnabled => BrowseSmokeMonitorReady && !_monitorPaused;
    internal void BrowseSmokeSearch(string text) => SearchBox.Text = text;
    internal void BrowseSmokeReverseDateSort() => DateSortButton.RaiseEvent(
        new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, DateSortButton));

    // Exhaustive inspection is deliberately limited to this tiny owned fixture.
    // The production grid and catalog never materialize a whole personal library.
    internal PhotoItem[] BrowseSmokeSmallFixtureItems()
    {
        if (!LocalCatalogStore.IsIsolatedSmokeCatalog || _browseSmokeRoot is null ||
            !string.Equals(_currentQuery?.Folder, Path.Combine(_browseSmokeRoot, "SearchSort"), StringComparison.OrdinalIgnoreCase) ||
            PhotoRows is not VirtualPhotoRows rows || rows.ItemCount > 96 || rows.Count > 128)
            throw new InvalidOperationException("Exhaustive browse smoke inspection requires its small owned search/sort fixture.");
        for (var row = 0; row < rows.Count; row++) _ = rows[row];
        return rows.LoadedItems.OrderBy(item => item.ViewIndex).ToArray();
    }

    internal void BrowseSmokeScrollToItem(long index)
    {
        if (PhotoRows is not VirtualPhotoRows rows || index < 0 || index >= rows.ItemCount)
            throw new InvalidOperationException("Browse smoke scroll target is outside the current view.");
        var row = rows.RowForItem(index);
        PhotoGrid.ScrollIntoView(rows[row]);
        if (FindVisualChild<ScrollViewer>(PhotoGrid) is not { } scroll)
            throw new InvalidOperationException("Browse smoke grid has no ScrollViewer.");
        scroll.ScrollToVerticalOffset(row);
    }

    internal (long Index, string? Path) BrowseSmokeAnchor
    {
        get { var anchor = GetGridAnchor(); return (anchor.Index, anchor.Path); }
    }

    internal string[] BrowseSmokeTopRowPaths
    {
        get
        {
            if (PhotoRows is not VirtualPhotoRows { Count: > 0 } rows || FindVisualChild<ScrollViewer>(PhotoGrid) is not { } scroll)
                return [];
            var index = Math.Clamp((int)scroll.VerticalOffset, 0, rows.Count - 1);
            if (rows[index] is PhotoRow { IsHeader: true } && index + 1 < rows.Count) index++;
            return (rows[index] as PhotoRow)?.Items.Select(item => item.Path).ToArray() ?? [];
        }
    }

    internal PhotoItem BrowseSmokeSelectVisiblePhoto(string path)
    {
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None)
            throw new InvalidOperationException("Browse smoke selection requires no physical modifier keys.");
        System.Windows.Controls.Border? FindTile(DependencyObject parent)
        {
            if (parent is System.Windows.Controls.Border { ContextMenu: not null, IsVisible: true, DataContext: PhotoItem item } tile &&
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)) return tile;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (FindTile(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
            return null;
        }
        var target = FindTile(PhotoGrid) ?? throw new InvalidOperationException("Browse smoke photo tile has not been realized.");
        // A routed event on the actual rendered tile exercises the normal handler.
        // This is synthesized WPF input, not a physical mouse automation claim.
        target.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
            Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        var selected = (PhotoItem)target.DataContext;
        if (!BrowseSmokeSelectionRetained(selected)) throw new InvalidOperationException("Production photo selection did not select its tile.");
        return selected;
    }

    internal bool BrowseSmokeSelectionRetained(PhotoItem item) => item.IsSelected && _selection.Count == 1 &&
        ReferenceEquals(_selection.Find(item.Path), item) && ReferenceEquals(_selectedPhoto, item) &&
        (PhotoRows as VirtualPhotoRows)?.LoadedItems.Any(candidate => ReferenceEquals(candidate, item)) == true;

    internal bool BrowseSmokeDecodedPixels(string path)
    {
        bool Find(DependencyObject parent)
        {
            if (parent is PreviewImage image && image.IsLoaded && image.IsVisible && image.Source is BitmapSource source &&
                string.Equals(AsyncMediaImage.GetPath(image), path, StringComparison.OrdinalIgnoreCase))
            {
                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                var pixel = new byte[4];
                converted.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                return source.PixelWidth > 0 && source.PixelHeight > 0 && pixel[0] == 0 && pixel[1] == 0 && pixel[2] == 255 && pixel[3] == 255;
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (Find(VisualTreeHelper.GetChild(parent, index))) return true;
            return false;
        }
        return Find(PhotoGrid);
    }

    private sealed class BrowseSmokeCheckBox : CheckBox
    {
        internal void ClickForSmoke() => OnClick();
    }
}
