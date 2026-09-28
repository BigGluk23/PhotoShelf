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
        !_metadataTask.IsCompleted || !_monitorConfigurationTask.IsCompleted || (_libraryMonitor?.PendingPathCount ?? 0) > 0;
    internal bool BrowseSmokeWritersDrained => _closeReady && _scanTask.IsCompleted && _browseTask.IsCompleted && _metadataTask.IsCompleted;
    internal string[] BrowseSmokeLoadedPaths => (PhotoRows as VirtualPhotoRows)?.LoadedItems.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];

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
