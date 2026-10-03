using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoShelf.Application.Files;
using PhotoShelf.Infrastructure.Sqlite;
using Forms = System.Windows.Forms;

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
    private DuplicateSelectionSummary _selectionSummary = new(0, 0, 0);
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
        if (ItemList is not null) ItemList.SelectedItems.Clear();
        RefreshPaging();
    }

    private void OnCompareSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CompareSelectedButton is not null)
            CompareSelectedButton.IsEnabled = ItemList.SelectedItems.Count is >= 2 and <= 4;
    }

    private void OnCompareSelectedClicked(object sender, RoutedEventArgs e) => OpenSelectedComparison();

    private void OpenSelectedComparison()
    {
        var photos = ItemList.SelectedItems.Cast<DuplicateItemViewModel>().Select(item => item.Photo).Take(5).ToArray();
        if (photos.Length is < 2 or > 4)
        {
            StatusText.Text = "Для сравнения выделите от двух до четырёх файлов с Ctrl или Shift.";
            return;
        }
        new PhotoCompareWindow(photos) { Owner = this }.ShowDialog();
    }

    private async void OnItemListPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true; OpenSelectedComparison(); return;
        }
        if (_preparing || _moving || _editingSelection || _closingReview || _loadingPage) return;
        var selected = ItemList.SelectedItems.Cast<DuplicateItemViewModel>().ToArray();
        if (e.Key == Key.K && selected.Length == 1 && SelectedGroup is { } group)
        {
            e.Handled = true;
            await RunSessionEditAsync(() => _session.SetKeeperAsync(group.Index, selected[0].Photo.Path, _pageCancellation.Token),
                "Сохраняемая копия изменена для выбранной группы.");
            return;
        }
        if (e.Key == Key.Space)
        {
            var extras = selected.Where(item => !item.IsKeep).ToArray();
            if (extras.Length == 0) return;
            e.Handled = true;
            var mark = extras.Any(item => !item.IsSelected);
            await RunSessionEditAsync(() => _session.SetItemsSelectedAsync(extras.Select(item => item.Photo.Path).ToArray(),
                mark, _pageCancellation.Token), mark ? "Выбранные копии отмечены в карантин." : "С выбранных копий сняты отметки.");
        }
    }

    private async void OnKeepItemClicked(object sender, RoutedEventArgs e)
    {
        if (_preparing || _moving || _editingSelection || _closingReview || _loadingPage) return;
        if (sender is FrameworkElement { DataContext: DuplicateItemViewModel item } && SelectedGroup is { } group)
            await RunSessionEditAsync(() => _session.SetKeeperAsync(group.Index, item.Photo.Path, _pageCancellation.Token),
                "Сохраняемая копия изменена для выбранной группы.");
    }

    private async void OnKeepNewestClicked(object sender, RoutedEventArgs e) =>
        await RunSessionEditAsync(() => _session.ApplyKeeperRuleAsync(DuplicateKeeperRule.NewestFile,
            token: _pageCancellation.Token), "Во всех группах оставлена самая новая копия.");

    private async void OnKeepShortestPathClicked(object sender, RoutedEventArgs e) =>
        await RunSessionEditAsync(() => _session.ApplyKeeperRuleAsync(DuplicateKeeperRule.ShortestPath,
            token: _pageCancellation.Token), "Во всех группах оставлена копия с наиболее коротким путём.");

    private async void OnKeepPriorityFolderClicked(object sender, RoutedEventArgs e)
    {
        using var picker = new Forms.FolderBrowserDialog
        {
            Description = "Выберите папку, копии из которой нужно оставлять в первую очередь.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (picker.ShowDialog() != Forms.DialogResult.OK) return;
        var folder = Path.GetFullPath(picker.SelectedPath);
        await RunSessionEditAsync(() => _session.ApplyKeeperRuleAsync(DuplicateKeeperRule.PriorityFolder,
            folder, _pageCancellation.Token), $"Приоритет сохраняемой копии: {folder}");
    }

    private async void OnMarkExtrasClicked(object sender, RoutedEventArgs e) =>
        await RunSessionEditAsync(() => _session.SetAllExtrasSelectedAsync(true, _pageCancellation.Token),
            "Все лишние точные копии отмечены. Перед переносом будет показан полный план.");

    private async void OnClearMarksClicked(object sender, RoutedEventArgs e) =>
        await RunSessionEditAsync(() => _session.SetAllExtrasSelectedAsync(false, _pageCancellation.Token),
            "Все отметки сняты.");

    private async void OnMoveItemToggled(object sender, RoutedEventArgs e)
    {
        if (_preparing || _moving || _editingSelection || _closingReview || _loadingPage) return;
        if (sender is not FrameworkElement { DataContext: DuplicateItemViewModel item }) return;
        var requested = item.IsSelected;
        _editingSelection = true;
        UpdateEditingEnabled();
        try
        {
            await _session.SetItemSelectedAsync(item.Photo.Path, requested, _pageCancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            item.IsSelected = !requested;
            if (IsLoaded) StatusText.Text = $"Выбор не сохранён: {ex.Message}";
        }
        finally
        {
            _editingSelection = false;
            UpdateEditingEnabled();
            if (IsLoaded) await RefreshSelectionSummaryAsync();
        }
    }

    private async Task RunSessionEditAsync(Func<Task> edit, string successText)
    {
        if (_preparing || _moving || _editingSelection || _closingReview || _loadingPage) return;
        var selectedGroupId = SelectedGroup?.Index;
        _editingSelection = true; UpdateEditingEnabled();
        string? error = null;
        try { await edit(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = $"Выбор не сохранён: {ex.Message}"; }
        finally { _editingSelection = false; UpdateEditingEnabled(); }
        if (!IsLoaded || _closingReview) return;
        await LoadGroupsPageAsync(_groupOffset, selectedGroupId);
        if (error is not null) StatusText.Text = error;
        else StatusText.Text = successText;
    }

    private void UpdateEditingEnabled()
    {
        // Disable selection bindings too: guarding click handlers alone does not freeze CheckBox writes.
        // The title-bar close button and the separate progress/plan window remain available.
        if (Content is UIElement content)
            content.IsEnabled = !_preparing && !_moving && !_editingSelection && !_closingReview && !_loadingPage;
    }

    private sealed record QuarantineGroupSnapshot(long GroupId, string KeepPath, string Hash);

    private async Task<bool> CanStartQuarantineAsync()
    {
        if (_checkingRecovery) return false;
        _checkingRecovery = true;
        try
        {
            var directory = Path.Combine(LocalCatalogStore.CatalogDirectory, "operations");
            var history = await Task.Run(() => FileOperations.CreateService().ReadHistory(directory, _pageCancellation.Token));
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
            // Freeze the persisted snapshot, not just the visible page. The bounded session refuses
            // more than the durable operation limit before any filesystem plan is created.
            var candidates = await _session.ReadQuarantineSelectionAsync(token: _pageCancellation.Token);
            if (candidates.Count == 0)
            {
                StatusText.Text = "Сначала отметьте лишние точные копии.";
                return;
            }
            var protectedGroups = candidates.GroupBy(candidate => candidate.GroupId).Select(group =>
            {
                var first = group.First();
                if (group.Any(item => !item.KeeperPath.Equals(first.KeeperPath, StringComparison.OrdinalIgnoreCase) ||
                    !item.Hash.Equals(first.Hash, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Снимок выбора повреждён; повторите поиск точных дублей.");
                return new QuarantineGroupSnapshot(first.GroupId, first.KeeperPath, first.Hash);
            }).ToDictionary(group => group.GroupId);
            var candidateByPath = candidates.ToDictionary(candidate => candidate.Item.Path,
                StringComparer.OrdinalIgnoreCase);
            var expectedHashes = candidates.ToDictionary(candidate => candidate.Item.Path,
                candidate => candidate.Hash, StringComparer.OrdinalIgnoreCase);
            var quarantineRoot = await QuarantineConfiguration.GetOrChooseRootAsync(this);
            if (quarantineRoot is null || _closingReview || !IsLoaded) return;
            var batchRoot = Path.Combine(quarantineRoot, $"PhotoShelf-Quarantine-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{Guid.NewGuid():N}");
            var requests = candidates.Select(candidate => new MoveRequest(candidate.Item.Path, candidate.Item.CaptureDate)).ToArray();
            var dialog = new MovePlanWindow(requests, batchRoot, false, quarantineMode: true) { Owner = this };
            if (dialog.ShowDialog() != true || _closingReview || !IsLoaded) return;
            var plan = dialog.Plan;
            if (await _session.ContainsKeeperAsync(plan.Where(entry => entry.SkipReason is null).Select(entry => entry.Source), _pageCancellation.Token))
            {
                System.Windows.MessageBox.Show("План включает сохраняемую копию как связанный файл. Измените выбор; вся группа должна сохраняться вместе.", "Карантин отменён"); return;
            }
            if (_closingReview || !IsLoaded) return;
            plan = plan.Select(entry => expectedHashes.TryGetValue(entry.Source, out var hash) ? entry with { ExpectedHash = hash } : entry).ToArray();
            var duplicateGroupByMoveGroup = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var moveGroup in plan.GroupBy(entry => entry.GroupId ?? throw new IOException("План не содержит безопасную группу связанных файлов.")))
            {
                var duplicateGroups = moveGroup.Where(entry => candidateByPath.ContainsKey(entry.Source))
                    .Select(entry => candidateByPath[entry.Source].GroupId).Distinct().ToArray();
                if (duplicateGroups.Length != 1)
                    throw new IOException("Связанная группа пересекает несколько групп дублей. Автоматический карантин остановлен.");
                duplicateGroupByMoveGroup.Add(moveGroup.Key, duplicateGroups[0]);
            }
            // Persist the exact batch location after every read-only validation and before
            // any journal or media write, including before recovery callbacks.
            await QuarantineConfiguration.RegisterBatchAsync(batchRoot);
            if (_closingReview || !IsLoaded) return;
            _moving = true;
            _operationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource(); _moveCancellation = cancellation;
            var progressWindow = new FileOperationProgressWindow(cancellation) { Owner = this }; progressWindow.Show();
            try
            {
                using var mediaPause = await AsyncMediaImage.PauseForFileOperationsAsync(plan.SelectMany(x => new[] { x.Source, x.Destination }).ToHashSet(StringComparer.OrdinalIgnoreCase));
                var results = new List<MoveResult>(plan.Count);
                var journals = new List<string>();
                var completedBeforeBatch = 0;
                var batchNumber = 0;
                foreach (var duplicateGroupBatch in protectedGroups.Keys.Chunk(128))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var duplicateGroupIds = duplicateGroupBatch.ToHashSet();
                    var batchPlan = plan.Where(entry => entry.GroupId is { } moveGroupId &&
                        duplicateGroupIds.Contains(duplicateGroupByMoveGroup[moveGroupId])).ToArray();
                    if (batchPlan.Length == 0) continue;
                    var batchKeepers = duplicateGroupBatch.Select(id => protectedGroups[id]).ToArray();
                    var journal = Path.Combine(LocalCatalogStore.CatalogDirectory, "operations",
                        $"quarantine-{Path.GetFileName(batchRoot)}-{++batchNumber:D4}.jsonl");
                    journals.Add(journal);
                    var progressBase = completedBeforeBatch;
                    var progress = new Progress<int>(count => progressWindow.SetProgress(progressBase + count));
                    var batchResults = await Task.Run(async () =>
                    {
                        var locks = new List<FileStream>();
                        try
                        {
                            foreach (var group in batchKeepers)
                            {
                                // The verified surviving copy stays open without write/delete sharing
                                // for every filesystem group which can modify its exact duplicates.
                                var stream = new FileStream(group.KeepPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                    131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                                locks.Add(stream);
                                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation.Token));
                                if (!actual.Equals(group.Hash, StringComparison.OrdinalIgnoreCase))
                                    throw new IOException($"Сохраняемый файл изменился: {group.KeepPath}. Повторите поиск дублей.");
                            }
                            return await FileOperations.CreateService().ExecuteAsync(batchPlan, journal,
                                _commitCatalog, progress, cancellation.Token);
                        }
                        finally { foreach (var stream in locks) await stream.DisposeAsync(); }
                    });
                    results.AddRange(batchResults);
                    completedBeforeBatch += batchResults.Count;
                    var movedCandidates = batchResults.Where(result => result.Moved && candidateByPath.ContainsKey(result.Entry.Source))
                        .Select(result => result.Entry.Source).ToArray();
                    if (movedCandidates.Length > 0)
                        await _session.MarkMovedAsync(movedCandidates, _pageCancellation.Token);
                }
                var moved = results.Where(x => x.Moved).Select(x => x.Entry.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _reloadPageAfterMove = true;
                System.Windows.MessageBox.Show($"Перенесено файлов: {moved.Count:N0}\nНе завершено: {results.Count(x => !x.Moved):N0}\nКарантин: {batchRoot}\nЖурналов: {journals.Count:N0}\nВосстановление и откат доступны в окне «Операции».", "Карантин");
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
            page.TotalFiles, page.MemberOffset, page.KeeperPath, page.SelectedPaths);
    }

    private async Task LoadGroupsPageAsync(long offset, long? preferredGroupId = null)
    {
        if (_loadingPage || _preparing || _moving || _editingSelection || _closingReview || offset < 0 || offset >= _session.GroupCount) return;
        _loadingPage = true; UpdateEditingEnabled();
        try
        {
            var page = await _session.ReadGroupsAsync(offset, _pageCancellation.Token);
            var models = await Task.Run(() => page.Select(CreateGroup).ToArray(), _pageCancellation.Token);
            if (_closingReview) return;
            _groupOffset = offset; Groups.Clear(); foreach (var model in models) { ObserveSelection(model); Groups.Add(model); }
            var preferred = preferredGroupId is null ? null : Groups.FirstOrDefault(group => group.Index == preferredGroupId);
            GroupList.SelectedItem = preferred ?? Groups.FirstOrDefault();
            if (GroupList.SelectedItem is not null) GroupList.ScrollIntoView(GroupList.SelectedItem);
            await RefreshSelectionSummaryAsync();
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
            ObserveSelection(model); var index = Groups.IndexOf(selected); Groups[index] = model; GroupList.SelectedIndex = index;
            await RefreshSelectionSummaryAsync();
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
        SummaryText.Text = $"Групп в снимке: {_session.GroupCount:N0} · Отмечено: {_selectionSummary.SelectedFiles:N0} файлов в {_selectionSummary.SelectedGroups:N0} группах";
        StatusText.Text = $"Выбор сохраняется при перелистывании · Объём карантина: {_selectionSummary.SelectedBytes / 1048576d:N1} МиБ · Перед переносом будет полный план";
        RefreshPaging();
    }

    private async Task RefreshSelectionSummaryAsync()
    {
        try { _selectionSummary = await _session.ReadSelectionSummaryAsync(_pageCancellation.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (IsLoaded) StatusText.Text = $"Не удалось обновить итог выбора: {ex.Message}";
            return;
        }
        if (IsLoaded && !_closingReview) RefreshSummary();
    }
}
