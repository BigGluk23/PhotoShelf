using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using PhotoShelf.Application.Files;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Desktop;

public partial class DuplicateReviewWindow : Window, INotifyPropertyChanged
{
    private readonly Func<MoveEntry, Task> _commitCatalog;
    private readonly DuplicateSearchSession _session;
    private readonly CancellationTokenSource _pageCancellation = new();
    private bool _loadingPage;
    private long _groupOffset;
    private bool _reloadPageAfterMove;
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
        _pageCancellation.Cancel();
        UpdateEditingEnabled();
        _moveCancellation?.Cancel();
    }
    private DuplicateGroupViewModel? _selectedGroup;

    public DuplicateReviewWindow(DuplicateSearchSession session, Func<MoveEntry, Task> commitCatalog)
    {
        InitializeComponent();
        _commitCatalog = commitCatalog;
        _session = session;
        Groups = new();
        Loaded += async (_, _) => await LoadGroupsPageAsync(0);
        Closed += (_, _) => _pageCancellation.Cancel();
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

    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedGroup = GroupList.SelectedItem as DuplicateGroupViewModel;
        RefreshPaging();
    }

    private async void OnKeepItemClicked(object sender, RoutedEventArgs e)
    {
        if (_preparing || _moving || _editingSelection || _closingReview || _loadingPage) return;
        if (sender is FrameworkElement { DataContext: DuplicateItemViewModel item })
        {
            item.IsSelected = false;
            await EditSelectionAsync(group => { if (ReferenceEquals(group, SelectedGroup)) group.Keep(item); });
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
        if (_preparing || _moving || _editingSelection || _closingReview || _loadingPage) return;
        _editingSelection = true;
        string? selectionError = null;
        UpdateEditingEnabled();
        try
        {
            foreach (var batch in Groups.ToArray().Chunk(32))
            {
                if (_preparing || _moving || _closingReview || !IsLoaded) return;
                foreach (var group in batch)
                {
                    var previous = group.KeepPath;
                    edit(group);
                    try { await _session.SetKeeperAsync(group.Index, group.KeepPath, _pageCancellation.Token); }
                    catch { group.KeepItem = group.Items.FirstOrDefault(item => item.Photo.Path == previous); throw; }
                }
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { selectionError = $"Выбор не сохранён: {ex.Message}"; }
        finally
        {
            _editingSelection = false;
            UpdateEditingEnabled();
            if (IsLoaded) { RefreshSummary(); if (selectionError is not null) StatusText.Text = selectionError; }
        }
    }

    private void UpdateEditingEnabled()
    {
        // Disable selection bindings too: guarding click handlers alone does not freeze CheckBox writes.
        // The title-bar close button and the separate progress/plan window remain available.
        if (Content is UIElement content)
            content.IsEnabled = !_preparing && !_moving && !_editingSelection && !_closingReview && !_loadingPage;
    }

    private sealed record QuarantineGroupSnapshot(string KeepPath, string Hash, DuplicateItemViewModel[] Marked);

    private async Task<bool> CanStartQuarantineAsync()
    {
        if (_checkingRecovery) return false;
        _checkingRecovery = true;
        try
        {
            var directory = Path.Combine(LocalCatalogStore.CatalogDirectory, "operations");
            var history = await Task.Run(() => new FileMoveService().ReadHistory(directory, _pageCancellation.Token));
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
        if (_moving || _checkingRecovery || _preparing || _editingSelection || _closingReview || _loadingPage) return;
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
            var expectedHashes = protectedGroups.SelectMany(group => group.Marked.Select(item => (item.Photo.Path, group.Hash)))
                .ToDictionary(x => x.Path, x => x.Hash, StringComparer.OrdinalIgnoreCase);
            var quarantineRoot = await QuarantineConfiguration.GetOrChooseRootAsync(this);
            if (quarantineRoot is null || _closingReview || !IsLoaded) return;
            var batchRoot = Path.Combine(quarantineRoot, $"PhotoShelf-Quarantine-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{Guid.NewGuid():N}");
            var requests = marked.Select(x => new MoveRequest(x.Photo.Path, x.Photo.CaptureDate)).ToArray();
            var dialog = new MovePlanWindow(requests, batchRoot, false) { Owner = this };
            if (dialog.ShowDialog() != true || _closingReview || !IsLoaded) return;
            // Persist the exact batch location before any file can move, including before recovery callbacks.
            await QuarantineConfiguration.RegisterBatchAsync(batchRoot);
            if (_closingReview || !IsLoaded) return;
            var plan = dialog.Plan;
            if (await _session.ContainsKeeperAsync(plan.Where(entry => entry.SkipReason is null).Select(entry => entry.Source), _pageCancellation.Token))
            {
                System.Windows.MessageBox.Show("План включает сохраняемую копию как связанный файл. Измените выбор; вся группа должна сохраняться вместе.", "Карантин отменён"); return;
            }
            if (_closingReview || !IsLoaded) return;
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
                await _session.MarkMovedAsync(moved, _pageCancellation.Token);
                _reloadPageAfterMove = true;
                RemoveMovedItems(marked.Where(x => moved.Contains(x.Photo.Path)).ToArray());
                System.Windows.MessageBox.Show($"Перенесено: {moved.Count}\nНе завершено: {results.Count(x => !x.Moved)}\nКарантин: {batchRoot}\nВосстановление и откат: окно «Операции».\nЖурнал: {journal}", "Карантин");
                RefreshSummary();
            }
            catch (OperationCanceledException) { StatusText.Text = "Остановлено. Проверьте журнал в окне «Операции»."; }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Карантин: операция остановлена"); }
            finally { _moving = false; _moveCancellation = null; progressWindow.Finish(); _operationFinished.TrySetResult(); }
        }
        catch (OperationCanceledException) { if (IsLoaded) StatusText.Text = "Подготовка карантина отменена"; }
        catch (Exception ex) { if (IsLoaded && !_closingReview) System.Windows.MessageBox.Show(this, ex.Message, "Карантин не начат"); }
        finally
        {
            _preparing = false; UpdateEditingEnabled();
            if (_reloadPageAfterMove && !_closingReview)
            { _reloadPageAfterMove = false; await LoadGroupsPageAsync(_groupOffset); }
        }
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

    private async void OnOpenQuarantineClicked(object sender, RoutedEventArgs e)
    {
        var root = await QuarantineConfiguration.GetOrChooseRootAsync(this);
        if (root is null || !IsLoaded) return;
        if (!Directory.Exists(root)) { System.Windows.MessageBox.Show(this, $"Папка недоступна:\n{root}", "Карантин"); return; }
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{root}\"",
            UseShellExecute = true
        });
    }

    private async void OnPreviousGroupsClicked(object sender, RoutedEventArgs e) => await LoadGroupsPageAsync(Math.Max(0, _groupOffset - DuplicateSearchSession.GroupsPerPage));
    private async void OnNextGroupsClicked(object sender, RoutedEventArgs e) => await LoadGroupsPageAsync(_groupOffset + DuplicateSearchSession.GroupsPerPage);
    private async void OnPreviousMembersClicked(object sender, RoutedEventArgs e) => await LoadMembersPageAsync(-1);
    private async void OnNextMembersClicked(object sender, RoutedEventArgs e) => await LoadMembersPageAsync(1);

    private static DuplicateGroupViewModel CreateGroup(DuplicateGroupPage page)
    {
        var photos = page.Items.Select(saved =>
        {
            var photo = new PhotoItem(saved.Path, saved.SizeBytes, saved.FileModifiedAt);
            photo.ApplyIndexedCaptureDate(saved.CaptureDate); return photo;
        }).ToArray();
        return new DuplicateGroupViewModel(new DuplicateGroup(page.SizeBytes, page.Hash, photos), page.Id,
            page.TotalFiles, page.MemberOffset, page.KeeperPath);
    }

    private async Task LoadGroupsPageAsync(long offset)
    {
        if (_loadingPage || _preparing || _moving || _editingSelection || _closingReview || offset < 0 || offset >= _session.GroupCount) return;
        _loadingPage = true; UpdateEditingEnabled();
        try
        {
            var page = await _session.ReadGroupsAsync(offset, _pageCancellation.Token);
            var models = await Task.Run(() => page.Select(CreateGroup).ToArray(), _pageCancellation.Token);
            if (_closingReview) return;
            _groupOffset = offset; Groups.Clear(); foreach (var model in models) { ObserveSelection(model); Groups.Add(model); }
            GroupList.SelectedIndex = Groups.Count > 0 ? 0 : -1; RefreshSummary();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Не удалось загрузить страницу: {ex.Message}"; }
        finally { _loadingPage = false; UpdateEditingEnabled(); RefreshPaging(); }
    }

    private async Task LoadMembersPageAsync(int direction)
    {
        if (_loadingPage || _preparing || _moving || _editingSelection || _closingReview || SelectedGroup is not { } selected) return;
        var offset = Math.Max(0, selected.MemberOffset + direction * DuplicateSearchSession.MembersPerPage);
        if (offset >= selected.TotalFiles - 1) return;
        _loadingPage = true; UpdateEditingEnabled();
        try
        {
            var page = await _session.ReadGroupAsync(selected.Index, offset, _pageCancellation.Token);
            var model = await Task.Run(() => CreateGroup(page), _pageCancellation.Token);
            if (_closingReview) return;
            ObserveSelection(model); var index = Groups.IndexOf(selected); Groups[index] = model; GroupList.SelectedIndex = index; RefreshSummary();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Не удалось загрузить файлы: {ex.Message}"; }
        finally { _loadingPage = false; UpdateEditingEnabled(); RefreshPaging(); }
    }

    private void ObserveSelection(DuplicateGroupViewModel group) => group.PropertyChanged += (_, args) =>
    {
        if (args.PropertyName == nameof(DuplicateGroupViewModel.MoveCount) && !_loadingPage) RefreshSummary();
    };

    private void RefreshPaging()
    {
        if (PreviousGroupsButton is null) return;
        PreviousGroupsButton.IsEnabled = !_loadingPage && _groupOffset > 0;
        NextGroupsButton.IsEnabled = !_loadingPage && _groupOffset + DuplicateSearchSession.GroupsPerPage < _session.GroupCount;
        GroupsPageText.Text = $"{_groupOffset + 1:N0}–{Math.Min(_groupOffset + Groups.Count, _session.GroupCount):N0} / {_session.GroupCount:N0}";
        var group = SelectedGroup;
        PreviousMembersButton.IsEnabled = !_loadingPage && group is not null && group.MemberOffset > 0;
        NextMembersButton.IsEnabled = !_loadingPage && group is not null && group.MemberOffset + DuplicateSearchSession.MembersPerPage < group.TotalFiles - 1;
        MembersPageText.Text = group is null ? "" : $"Сохраняемая копия + {group.Items.Count - 1} из {Math.Max(0, group.TotalFiles - 1):N0} · блок {group.MemberOffset / DuplicateSearchSession.MembersPerPage + 1}";
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RefreshSummary()
    {
        SummaryText.Text = $"Групп в снимке: {_session.GroupCount:N0} · На странице: {Groups.Count} · Выбор действует только на загруженные файлы";
        StatusText.Text = $"Отмечено на странице: {Groups.Sum(static group => group.MoveCount)} · При смене страницы отметки сбрасываются";
        RefreshPaging();
    }
}
