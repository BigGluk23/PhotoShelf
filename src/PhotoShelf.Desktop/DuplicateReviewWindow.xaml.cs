using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PhotoShelf.Application.Files;

namespace PhotoShelf.Desktop;

public partial class DuplicateReviewWindow : Window, INotifyPropertyChanged
{
    private readonly Func<MoveEntry, Task> _commitCatalog;
    private bool _moving;
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
        if (sender is FrameworkElement { DataContext: DuplicateItemViewModel item })
        {
            item.IsSelected = false;
            SelectedGroup?.Keep(item);
            RefreshSummary();
        }
    }

    private async void OnKeepNewestClicked(object sender, RoutedEventArgs e)
    {
        if (_moving) return;
        foreach (var batch in Groups.ToArray().Chunk(32))
        {
            foreach (var group in batch) { group.KeepNewest(); }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        }
        RefreshSummary();
    }

    private async void OnKeepShortestPathClicked(object sender, RoutedEventArgs e)
    {
        if (_moving) return;
        foreach (var batch in Groups.ToArray().Chunk(32))
        {
            foreach (var group in batch) { group.KeepShortestPath(); }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        }
        RefreshSummary();
    }

    private async void OnKeepLargestFileClicked(object sender, RoutedEventArgs e)
    {
        if (_moving) return;
        foreach (var batch in Groups.ToArray().Chunk(32))
        {
            foreach (var group in batch) { group.KeepLargestFile(); }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        }
        RefreshSummary();
    }

    private async void OnMarkExtrasClicked(object sender, RoutedEventArgs e)
    {
        foreach (var group in Groups.ToArray())
        {
            foreach (var item in group.Items)
            {
                item.IsSelected = !item.IsKeep;
            }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        }

        RefreshSummary();
    }

    private async void OnMoveMarkedClicked(object sender, RoutedEventArgs e)
    {
        if (_moving) return;
        var marked = Groups.SelectMany(group => group.GetMarkedItems()).ToArray();
        if (marked.Length == 0) return;
        var batchRoot = Path.Combine(QuarantineRoot, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{Guid.NewGuid():N}");
        var requests = marked.Select(x => new MoveRequest(x.Photo.Path, x.Photo.CaptureDate)).ToArray();
        var dialog = new MovePlanWindow(requests, batchRoot, false) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var plan = dialog.Plan;
        _moving = true;
        try
        {
            var journal = Path.Combine(LocalCatalogStore.CatalogDirectory, "operations", $"quarantine-{Guid.NewGuid():N}.jsonl");
            var results = await Task.Run(() => new FileMoveService().ExecuteAsync(plan, journal, _commitCatalog, null, CancellationToken.None));
            var moved = results.Where(x => x.Moved).Select(x => x.Entry.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            RemoveMovedItems(marked.Where(x => moved.Contains(x.Photo.Path)).ToArray());
            System.Windows.MessageBox.Show($"Перенесено: {moved.Count}\nПропуски/ошибки: {results.Count - moved.Count}\nКарантин: {batchRoot}\nЖурнал: {journal}", "Карантин");
            RefreshSummary();
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Карантин: операция остановлена"); }
        finally { _moving = false; }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_moving) { e.Cancel = true; StatusText.Text = "Дождитесь завершения безопасного переноса"; }
        base.OnClosing(e);
    }

    private void RemoveMovedItems(IReadOnlyCollection<DuplicateItemViewModel> movedItems)
    {
        foreach (var item in movedItems)
        {
            var group = Groups.FirstOrDefault(group => group.Items.Contains(item));
            group?.Items.Remove(item);
        }

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
