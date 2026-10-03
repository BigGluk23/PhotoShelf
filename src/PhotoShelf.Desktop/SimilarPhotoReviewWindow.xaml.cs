using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Desktop;

public partial class SimilarPhotoReviewWindow : Window, INotifyPropertyChanged
{
    private readonly SimilarPhotoSearchSession _session;
    private readonly long _candidateItemCount;
    private readonly bool _backgroundIndexing;
    private readonly CancellationTokenSource _lifetime = new();
    private SimilarPhotoGroupViewModel? _selectedGroup;
    private long _groupOffset;
    private bool _loading;

    public SimilarPhotoReviewWindow(SimilarPhotoSearchSession session, long candidateItemCount,
        bool backgroundIndexing)
    {
        InitializeComponent();
        _session = session;
        _candidateItemCount = candidateItemCount;
        _backgroundIndexing = backgroundIndexing;
        Groups = new();
        DataContext = this;
        Loaded += async (_, _) => await LoadGroupsPageAsync(0);
        Closed += (_, _) => _lifetime.Cancel();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SimilarPhotoGroupViewModel> Groups { get; }

    public SimilarPhotoGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        private set
        {
            if (ReferenceEquals(_selectedGroup, value)) return;
            _selectedGroup = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedGroup)));
        }
    }

    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedGroup = GroupList.SelectedItem as SimilarPhotoGroupViewModel;
        RefreshPaging();
    }

    private async void OnPreviousGroupsClicked(object sender, RoutedEventArgs e) =>
        await LoadGroupsPageAsync(Math.Max(0, _groupOffset - SimilarPhotoSearchSession.GroupsPerPage));

    private async void OnNextGroupsClicked(object sender, RoutedEventArgs e) =>
        await LoadGroupsPageAsync(_groupOffset + SimilarPhotoSearchSession.GroupsPerPage);

    private async void OnPreviousMembersClicked(object sender, RoutedEventArgs e) => await LoadMembersPageAsync(-1);
    private async void OnNextMembersClicked(object sender, RoutedEventArgs e) => await LoadMembersPageAsync(1);

    private async Task LoadGroupsPageAsync(long offset)
    {
        if (_loading || offset < 0 || offset >= _session.GroupCount) return;
        _loading = true;
        RefreshPaging();
        try
        {
            var pages = await _session.ReadGroupsAsync(offset, _lifetime.Token);
            var models = await Task.Run(() => pages.Select(CreateGroup).ToArray(), _lifetime.Token);
            _groupOffset = offset;
            Groups.Clear();
            foreach (var model in models) Groups.Add(model);
            GroupList.SelectedIndex = Groups.Count > 0 ? 0 : -1;
            SummaryText.Text = $"Серий: {_session.GroupCount:N0} · В визуальном индексе: {_session.IndexedItemCount:N0} из {_candidateItemCount:N0}";
            StatusText.Text = _backgroundIndexing
                ? "Фоновый индекс ещё пополняется. Следующий поиск сможет показать дополнительные серии. Ничего не удаляется автоматически."
                : "Ничего не удаляется автоматически. Откройте исходную папку и сравните важные кадры крупно.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Не удалось загрузить страницу: {ex.Message}"; }
        finally
        {
            _loading = false;
            RefreshPaging();
        }
    }

    private async Task LoadMembersPageAsync(int direction)
    {
        if (_loading || SelectedGroup is not { } selected) return;
        var offset = Math.Max(0, selected.MemberOffset + direction * SimilarPhotoSearchSession.MembersPerPage);
        if (offset >= selected.TotalFiles - 1) return;
        _loading = true;
        RefreshPaging();
        try
        {
            var page = await _session.ReadGroupAsync(selected.Id, offset, _lifetime.Token);
            var model = await Task.Run(() => CreateGroup(page), _lifetime.Token);
            var index = Groups.IndexOf(selected);
            Groups[index] = model;
            GroupList.SelectedIndex = index;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Не удалось загрузить фотографии: {ex.Message}"; }
        finally
        {
            _loading = false;
            RefreshPaging();
        }
    }

    private static SimilarPhotoGroupViewModel CreateGroup(SimilarPhotoGroupPage page)
    {
        var items = page.Items.Select(member =>
        {
            var photo = new PhotoItem(member.Item.Path, member.Item.SizeBytes, member.Item.FileModifiedAt);
            photo.ApplyIndexedCaptureDate(member.Item.CaptureDate);
            return new SimilarPhotoItemViewModel(photo, member.DifferenceDistance,
                member.AverageDistance, member.IsReference);
        });
        return new(page.Id, page.TotalFiles, page.ReferencePath, page.MaximumDifferenceDistance,
            page.MaximumAverageDistance, page.MemberOffset, items);
    }

    private void RefreshPaging()
    {
        if (PreviousGroupsButton is null) return;
        PreviousGroupsButton.IsEnabled = !_loading && _groupOffset > 0;
        NextGroupsButton.IsEnabled = !_loading && _groupOffset + SimilarPhotoSearchSession.GroupsPerPage < _session.GroupCount;
        GroupsPageText.Text = Groups.Count == 0 ? "" :
            $"{_groupOffset + 1:N0}–{Math.Min(_groupOffset + Groups.Count, _session.GroupCount):N0} / {_session.GroupCount:N0}";
        var group = SelectedGroup;
        PreviousMembersButton.IsEnabled = !_loading && group is not null && group.MemberOffset > 0;
        NextMembersButton.IsEnabled = !_loading && group is not null &&
            group.MemberOffset + SimilarPhotoSearchSession.MembersPerPage < group.TotalFiles - 1;
        MembersPageText.Text = group is null ? "" :
            $"Эталон + {group.Items.Count - 1} из {Math.Max(0, group.TotalFiles - 1):N0}";
    }

    private void OnShowInFolderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || !File.Exists(path))
        {
            StatusText.Text = "Файл больше недоступен. Обновите каталог и повторите поиск.";
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
