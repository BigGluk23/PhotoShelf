using System.IO;
using System.Windows;
using System.Windows.Input;
using PhotoShelf.Application.Files;
using Forms = System.Windows.Forms;

namespace PhotoShelf.Desktop;

public partial class MainWindow
{
    private readonly HashSet<PhotoItem> _selection = new();
    private System.Windows.Point _dragStart;
    private bool _fileOperationActive;
    private Task _fileOperationTask = Task.CompletedTask;

    private void OnPhotoTileMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _selection.Count == 0 || _fileOperationActive) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        System.Windows.DragDrop.DoDragDrop((DependencyObject)sender, new System.Windows.DataObject("PhotoShelf.Selection", _selection.ToArray()), System.Windows.DragDropEffects.Move);
        e.Handled = true;
    }

    private void OnFolderDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = !_fileOperationActive && e.Data.GetDataPresent("PhotoShelf.Selection") ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFolderDrop(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true;
        if (_fileOperationActive || sender is not FrameworkElement { DataContext: FolderNode node } || node.IsPlaceholder || e.Data.GetData("PhotoShelf.Selection") is not PhotoItem[] items) return;
        await PreviewMoveAsync(items, node.FullPath, false);
    }

    private async void OnOrganizeYearsClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOperationActive || _selection.Count == 0) return;
        using var dialog = new Forms.FolderBrowserDialog { Description = "Корневая папка для раскладки по дате съёмки", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) await PreviewMoveAsync(_selection.ToArray(), dialog.SelectedPath, true);
    }

    private async Task PreviewMoveAsync(PhotoItem[] items, string destination, bool byYear)
    {
        if (_isCatalogLoading || !_catalogLoaded)
        {
            System.Windows.MessageBox.Show("Дождитесь загрузки каталога перед переносом файлов.", "PhotoShelf"); return;
        }
        var requests = items.Select(x => new MoveRequest(x.Path, x.CaptureDate)).ToArray();
        var dialog = new MovePlanWindow(requests, destination, byYear) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _fileOperationActive = true;
        _saveCancellation?.Cancel();
        _scanCancellation?.Cancel();
        _browseCancellation?.Cancel();
        _metadataIndexCancellation?.Cancel();
        var journal = Path.Combine(LocalCatalogStore.CatalogDirectory, "operations", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        _fileOperationTask = ExecuteMoveAsync(dialog.Plan, journal);
        try { await _fileOperationTask; }
        finally { _fileOperationActive = false; SaveCatalogState(); RebuildRows(); }
    }

    private async Task ExecuteMoveAsync(IReadOnlyList<MoveEntry> plan, string journal)
    {
        try
        {
            await _pendingSave;
            await PersistStateAsync(CaptureState(), CancellationToken.None, _catalogRevision);
            var progress = new Progress<int>(done => StatusText.Text = $"Перенос: {done} / {plan.Count}");
            var result = await Task.Run(() => new FileMoveService().ExecuteAsync(plan, journal, async entry =>
            {
                await _saveGate.WaitAsync();
                try { await _desktopCatalogStore.MoveItemAsync(entry.Source, entry.Destination); }
                finally { _saveGate.Release(); }
                var replacement = new PhotoItem(entry.Destination);
                await Dispatcher.InvokeAsync(() => ApplyMovedItem(entry.Source, replacement));
            }, progress, _lifetime.Token));
            System.Windows.MessageBox.Show($"Перенесено: {result.Count(x => x.Moved)}\nПропущено/ошибок: {result.Count(x => !x.Moved)}\nЖурнал: {journal}", "Перенос завершён");
        }
        catch (Exception ex) { System.Windows.MessageBox.Show($"Операция остановлена: {ex.Message}\nЖурнал восстановления: {journal}", "Перенос"); }
    }

    private void ApplyMovedItem(string source, PhotoItem? replacement)
    {
        var old = Photos.FirstOrDefault(x => x.Path.Equals(source, StringComparison.OrdinalIgnoreCase));
        if (old is not null)
        {
            if (replacement is not null) { replacement.IsFavorite = old.IsFavorite; if (old.IsCaptureDateLoaded) replacement.ApplyIndexedCaptureDate(old.CaptureDate); }
            _selection.Remove(old); Photos.Remove(old);
            if (_selectedPhoto == old) _selectedPhoto = replacement;
            if (_folderNodes.TryGetValue(NormalizePath(old.Folder), out var folder)) folder.DirectItemCount = Math.Max(0, folder.DirectItemCount - 1);
        }
        _catalogRevision++;
        _itemsByPath.Remove(source);
        _knownPhotoPaths.Remove(source); _duplicateHashCache.Remove(source);
        if (replacement is not null) ApplyItems(new[] { replacement });
    }
}
