using System.Windows;
using System.Windows.Media;

namespace PhotoShelf.Desktop;

public partial class MainWindow
{
    private readonly TaskCompletionSource<Exception?> _initialCatalogReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<Exception?> InitialCatalogReady => _initialCatalogReady.Task;
    private void MarkInitialCatalogReady() => _initialCatalogReady.TrySetResult(null);
    private void MarkInitialCatalogFailed(Exception error) => _initialCatalogReady.TrySetResult(error);

    internal bool HasVisibleSmokePreview(string fixturePath)
    {
        bool Find(DependencyObject parent)
        {
            if (parent is PreviewImage image && image.IsLoaded && image.IsVisible && image.Source is not null &&
                string.Equals(AsyncMediaImage.GetPath(image), fixturePath, StringComparison.OrdinalIgnoreCase)) return true;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (Find(VisualTreeHelper.GetChild(parent, i))) return true;
            return false;
        }
        return Find(PhotoGrid);
    }
}
