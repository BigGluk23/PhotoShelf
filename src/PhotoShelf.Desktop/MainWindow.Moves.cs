using System.IO;
using System.Windows;
using System.Windows.Input;
using PhotoShelf.Application.Files;
using Forms = System.Windows.Forms;

namespace PhotoShelf.Desktop;

public partial class MainWindow
{
    private readonly PhotoSelection _selection = new();
    private System.Windows.Point _dragStart;
    private bool _fileOperationActive;
    private bool _hasPendingRecovery = true;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _movedPaths = new();
    private Task _fileOperationTask = Task.CompletedTask;
    private CancellationTokenSource? _fileOperationCancellation;
    private static string OperationsDirectory => Path.Combine(LocalCatalogStore.CatalogDirectory, "operations");

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
        if (!_catalogLoaded || _closing) return;
        if (_hasPendingRecovery)
        {
            System.Windows.MessageBox.Show("Сначала проверьте незавершённые операции в окне «Операции». Новые переносы временно заблокированы.", "Восстановление"); return;
        }
        var requests = items.Select(x => new MoveRequest(x.Path, x.CaptureDate)).ToArray();
        var dialog = new MovePlanWindow(requests, destination, byYear) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var journal = Path.Combine(OperationsDirectory, $"move-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        await RunFileOperationAsync((progress, token) => new FileMoveService().ExecuteAsync(dialog.Plan, journal, CommitFileMoveAsync, progress, token), dialog.Plan.SelectMany(x => new[] { x.Source, x.Destination }).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
    private async Task CommitFileMoveAsync(MoveEntry entry)
    {
        await _desktopCatalogStore.MoveItemAsync(entry.Source, entry.Destination,
            removeFromLibrary: await QuarantineConfiguration.IsQuarantineDestinationAsync(entry.Destination));
        // UI bookkeeping never participates in the durable commit/recovery protocol.
        _movedPaths.Enqueue(entry.Source);
    }
    private void ApplyMovedPaths()
    {
        while (_movedPaths.TryDequeue(out var source))
        {
            var old = _selection.Find(source);
            if (old is not null) { _selection.Remove(old); old.IsSelected = false; }
            if (_selectedPhoto?.Path.Equals(source, StringComparison.OrdinalIgnoreCase) == true) _selectedPhoto = null;
        }
        SelectedText.Text = $"Выбрано: {_selection.Count:N0}";
    }

    private Task RunFileOperationAsync(Func<IProgress<int>, CancellationToken, Task<IReadOnlyList<MoveResult>>> action, IReadOnlySet<string>? affectedPaths = null)
    {
        if (_fileOperationActive || _closing) return Task.CompletedTask;
        _fileOperationActive = true;
        return _fileOperationTask = RunFileOperationCoreAsync(action, affectedPaths);
    }
    private async Task RunFileOperationCoreAsync(Func<IProgress<int>, CancellationToken, Task<IReadOnlyList<MoveResult>>> action, IReadOnlySet<string>? affectedPaths)
    {
        await Task.Yield();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _fileOperationCancellation = cancellation;
        var progressWindow = new FileOperationProgressWindow(cancellation) { Owner = this };
        progressWindow.Show();
        IDisposable? mediaPause = null;
        try
        {
            await StopCatalogWritersAsync();
            foreach (var viewer in OwnedWindows.OfType<PhotoViewerWindow>().ToArray()) viewer.Close();
            mediaPause = await AsyncMediaImage.PauseForFileOperationsAsync(affectedPaths);
            var progress = new Progress<int>(done => { StatusText.Text = $"Обработано файлов: {done:N0}"; progressWindow.SetProgress(done); });
            IReadOnlyList<MoveResult>? result = null;
            result = await Task.Run(() => action(progress, cancellation.Token));
            if (result is not null)
            {
                var errors = result.Where(x => !x.Moved).Select(x => $"{x.Entry.Source}: {x.Error}").Take(6);
                System.Windows.MessageBox.Show($"Завершено: {result.Count(x => x.Moved)}\nНе завершено: {result.Count(x => !x.Moved)}\n{string.Join("\n", errors)}\nИстория, восстановление и откат доступны в «Операции».", "Файловая операция");
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "Остановлено. Незавершённые шаги доступны в «Операции»."; }
        catch (Exception ex) { System.Windows.MessageBox.Show($"Операция остановлена: {ex.Message}\nОткройте «Операции» для восстановления. Не удаляйте временные файлы вручную.", "PhotoShelf"); }
        finally
        {
            ApplyMovedPaths();
            try { await CheckPendingOperationsAsync(); await RefreshCatalogCountAsync(); await RebuildRowsAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { StatusText.Text = $"Не удалось обновить состояние операции: {ex.Message}"; }
            finally
            {
                mediaPause?.Dispose();
                _fileOperationActive = false; _fileOperationCancellation = null; progressWindow.Finish();
            }
        }
    }
    private async Task CheckPendingOperationsAsync()
    {
        _hasPendingRecovery = true;
        var operations = await Task.Run(() => new FileMoveService().ReadHistory(OperationsDirectory));
        _hasPendingRecovery = operations.Any(x => x.PendingFiles > 0 || x.Error is not null);
        if (_hasPendingRecovery) StatusText.Text = "Есть незавершённые операции — откройте «Операции»";
    }
    private void OnOperationsClicked(object sender, RoutedEventArgs e)
    {
        if (_fileOperationActive || _closing) return;
        var window = new OperationsWindow(OperationsDirectory, async (history, undo) =>
        {
            var service = new FileMoveService();
            var undoJournal = Path.Combine(OperationsDirectory, $"undo-{Guid.NewGuid():N}.jsonl");
            var entries = await Task.Run(() => service.ReadPlan(history.JournalPath));
            var paths = entries.SelectMany(x => new[] { x.Source, x.Destination }).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await RunFileOperationAsync((progress, token) => undo
                ? service.UndoAsync(history.JournalPath, undoJournal, CommitFileMoveAsync, progress, token)
                : service.RecoverAsync(history.JournalPath, CommitFileMoveAsync, progress, token), paths);
        }, () => _desktopCatalogStore.CreateBackupAsync(_lifetime.Token)) { Owner = this };
        window.ShowDialog();
    }
}
