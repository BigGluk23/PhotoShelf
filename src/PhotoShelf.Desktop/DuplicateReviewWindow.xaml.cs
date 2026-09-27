using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using PhotoShelf.Application.Files;

namespace PhotoShelf.Desktop;

public partial class DuplicateReviewWindow : Window, INotifyPropertyChanged
{
    private readonly Func<MoveEntry, Task> _commitCatalog;
    private bool _moving;
    private bool _checkingRecovery;
    private bool _preparing;
    private bool _editingSelection;
    private bool _closingReview;
    private CancellationTokenSource? _moveCancellation;
    private TaskCompletionSource? _operationFinished;
    public Task PendingOperation => _operationFinished?.Task ?? Task.CompletedTask;
    public void StopOperation()
    {
        // The owner is closing: a pending history/plan continuation must not start a transfer.
        _closingReview = true;
        UpdateEditingEnabled();
        _moveCancellation?.Cancel();
    }
    private DuplicateGroupViewModel? _selectedGroup;

    public DuplicateReviewWindow(ObservableCollection<DuplicateGroupViewModel> groups, Func<MoveEntry, Task> commitCatalog)
    {
        InitializeComponent();
        _commitCatalog = commitCatalog;
        Groups = groups;
        DataContext = this;
        GroupList.SelectedIndex = Groups.Count > 0 ? 0 : -1;
        RefreshSummary();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; }

    public DuplicateGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        private set
        {
            _selectedGroup = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedGroup)));
        }
    }

    private static string QuarantineRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PhotoShelf_Quarantine");

    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedGroup = GroupList.SelectedItem as DuplicateGroupViewModel;
    }

    private void OnKeepItemClicked(object sender, RoutedEventArgs e)
    {
        if (_preparing || _moving || _editingSelection || _closingReview) return;
        if (sender is FrameworkElement { DataContext: DuplicateItemViewModel item })
        {
            item.IsSelected = false;
            SelectedGroup?.Keep(item);
            RefreshSummary();
        }
    }

    private async void OnKeepNewestClicked(object sender, RoutedEventArgs e) =>
        await EditSelectionAsync(group => group.KeepNewest());

    private async void OnKeepShortestPathClicked(object sender, RoutedEventArgs e) =>
        await EditSelectionAsync(group => group.KeepShortestPath());

    private async void OnKeepLargestFileClicked(object sender, RoutedEventArgs e) =>
        await EditSelectionAsync(group => group.KeepLargestFile());

    private async void OnMarkExtrasClicked(object sender, RoutedEventArgs e) =>
        await EditSelectionAsync(group =>
        {
            foreach (var item in group.Items) item.IsSelected = !item.IsKeep;
        });

    private async Task EditSelectionAsync(Action<DuplicateGroupViewModel> edit)
    {
        if (_preparing || _moving || _editingSelection || _closingReview) return;
        _editingSelection = true;
        UpdateEditingEnabled();
        try
        {
            foreach (var batch in Groups.ToArray().Chunk(32))
            {
                if (_preparing || _moving || _closingReview || !IsLoaded) return;
                foreach (var group in batch) edit(group);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        finally
        {
            _editingSelection = false;
            UpdateEditingEnabled();
            if (IsLoaded) RefreshSummary();
        }
    }

    private void UpdateEditingEnabled()
    {
        // Disable selection bindings too: guarding click handlers alone does not freeze CheckBox writes.
        // The title-bar close button and the separate progress/plan window remain available.
        if (Content is UIElement content)
            content.IsEnabled = !_preparing && !_moving && !_editingSelection && !_closingReview;
    }

    private sealed record QuarantineGroupSnapshot(string KeepPath, string Hash, DuplicateItemViewModel[] Marked);

    private async Task<bool> CanStartQuarantineAsync()
    {
        if (_checkingRecovery) return false;
        _checkingRecovery = true;
        try
        {
            var directory = Path.Combine(LocalCatalogStore.CatalogDirectory, "operations");
            var history = await Task.Run(() => new FileMoveService().ReadHistory(directory));
            if (!IsLoaded) return false;
            if (history.Any(operation => operation.PendingFiles > 0 || operation.Error is not null))
            {
                System.Windows.MessageBox.Show(this,
                    "Есть незавершённая или повреждённая операция. Закройте разбор дублей и проверьте её в окне «Операции» перед новым переносом в карантин.",
                    "Сначала восстановите операцию");
                return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            if (IsLoaded) System.Windows.MessageBox.Show(this,
                $"Не удалось проверить журналы: {exception.Message}\nНовый перенос не начат.", "Карантин недоступен");
            return false;
        }
        finally { _checkingRecovery = false; }
    }

    private async void OnMoveMarkedClicked(object sender, RoutedEventArgs e)
    {
        if (_moving || _checkingRecovery || _preparing || _editingSelection || _closingReview) return;
        _preparing = true;
        UpdateEditingEnabled();
        try
        {
            // Recheck every attempt, including attempts after cancellation/failure in this same review window.
            if (!await CanStartQuarantineAsync() || _closingReview) return;
            // Freeze all choices before opening the modal preview. No worker reads mutable view models.
            var protectedGroups = Groups.Select(group => new QuarantineGroupSnapshot(
                group.KeepPath, group.Hash, group.GetMarkedItems().ToArray()))
                .Where(group => group.Marked.Length > 0).ToArray();
            var marked = protectedGroups.SelectMany(group => group.Marked).ToArray();
            if (marked.Length == 0) return;
            var keepers = Groups.Select(group => group.KeepPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedHashes = protectedGroups.SelectMany(group => group.Marked.Select(item => (item.Photo.Path, group.Hash)))
                .ToDictionary(x => x.Path, x => x.Hash, StringComparer.OrdinalIgnoreCase);
            var batchRoot = Path.Combine(QuarantineRoot, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{Guid.NewGuid():N}");
            var requests = marked.Select(x => new MoveRequest(x.Photo.Path, x.Photo.CaptureDate)).ToArray();
            var dialog = new MovePlanWindow(requests, batchRoot, false) { Owner = this };
            if (dialog.ShowDialog() != true || _closingReview || !IsLoaded) return;
            var plan = dialog.Plan;
            if (plan.Any(entry => entry.SkipReason is null && keepers.Contains(entry.Source)))
            {
                System.Windows.MessageBox.Show("План включает сохраняемую копию как связанный файл. Измените выбор; вся группа должна сохраняться вместе.", "Карантин отменён"); return;
            }
            plan = plan.Select(entry => expectedHashes.TryGetValue(entry.Source, out var hash) ? entry with { ExpectedHash = hash } : entry).ToArray();
            _moving = true;
            _operationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource(); _moveCancellation = cancellation;
            var progressWindow = new FileOperationProgressWindow(cancellation) { Owner = this }; progressWindow.Show();
            try
            {
                using var mediaPause = await AsyncMediaImage.PauseForFileOperationsAsync(plan.SelectMany(x => new[] { x.Source, x.Destination }).ToHashSet(StringComparer.OrdinalIgnoreCase));
                var journal = Path.Combine(LocalCatalogStore.CatalogDirectory, "operations", $"quarantine-{Guid.NewGuid():N}.jsonl");
                var progress = new Progress<int>(count => progressWindow.SetProgress(count));
                var results = await Task.Run(async () =>
                {
                    var locks = new List<FileStream>();
                    try
                    {
                        foreach (var group in protectedGroups)
                        {
                            // Keep a verified surviving copy open without write/delete sharing until quarantine finishes.
                            var stream = new FileStream(group.KeepPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            locks.Add(stream);
                            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation.Token));
                            if (!actual.Equals(group.Hash, StringComparison.OrdinalIgnoreCase)) throw new IOException($"Сохраняемый файл изменился: {group.KeepPath}. Повторите поиск дублей.");
                        }
                        return await new FileMoveService().ExecuteAsync(plan, journal, _commitCatalog, progress, cancellation.Token);
                    }
                    finally { foreach (var stream in locks) await stream.DisposeAsync(); }
                });
                var moved = results.Where(x => x.Moved).Select(x => x.Entry.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
                RemoveMovedItems(marked.Where(x => moved.Contains(x.Photo.Path)).ToArray());
                System.Windows.MessageBox.Show($"Перенесено: {moved.Count}\nНе завершено: {results.Count(x => !x.Moved)}\nКарантин: {batchRoot}\nВосстановление и откат: окно «Операции».\nЖурнал: {journal}", "Карантин");
                RefreshSummary();
            }
            catch (OperationCanceledException) { StatusText.Text = "Остановлено. Проверьте журнал в окне «Операции»."; }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Карантин: операция остановлена"); }
            finally { _moving = false; _moveCancellation = null; progressWindow.Finish(); _operationFinished.TrySetResult(); }
        }
        finally { _preparing = false; UpdateEditingEnabled(); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_moving) { _moveCancellation?.Cancel(); e.Cancel = true; StatusText.Text = "Останавливаю на безопасной границе…"; }
        else _closingReview = true;
        base.OnClosing(e);
    }

    private void RemoveMovedItems(IReadOnlyCollection<DuplicateItemViewModel> movedItems)
    {
        var moved = movedItems.Select(x => x.Photo.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups)
            for (var i = group.Items.Count - 1; i >= 0; i--)
                if (moved.Contains(group.Items[i].Photo.Path)) group.Items.RemoveAt(i);

        for (var index = Groups.Count - 1; index >= 0; index--)
        {
            if (Groups[index].Items.Count < 2)
            {
                Groups.RemoveAt(index);
            }
        }

        if (Groups.Count > 0 && GroupList.SelectedIndex < 0)
        {
            GroupList.SelectedIndex = 0;
        }
    }

    private void OnOpenQuarantineClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(QuarantineRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{QuarantineRoot}\"",
            UseShellExecute = true
        });
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RefreshSummary()
    {
        SummaryText.Text = $"Групп: {Groups.Count}   Лишних файлов: {Groups.Sum(static group => group.Items.Count - 1)}";
        StatusText.Text = $"Отмечено в карантин: {Groups.Sum(static group => group.MoveCount)}";
    }
}
