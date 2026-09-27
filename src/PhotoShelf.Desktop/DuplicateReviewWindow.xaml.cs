using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PhotoShelf.Desktop;

public partial class DuplicateReviewWindow : Window, INotifyPropertyChanged
{
    private DuplicateGroupViewModel? _selectedGroup;

    public DuplicateReviewWindow(IReadOnlyList<DuplicateGroup> groups)
    {
        InitializeComponent();
        Groups = new ObservableCollection<DuplicateGroupViewModel>(
            groups.Select((group, index) => new DuplicateGroupViewModel(group, index + 1)));
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

    private void OnKeepNewestClicked(object sender, RoutedEventArgs e)
    {
        foreach (var group in Groups)
        {
            group.KeepNewest();
        }

        RefreshSummary();
    }

    private void OnKeepShortestPathClicked(object sender, RoutedEventArgs e)
    {
        foreach (var group in Groups)
        {
            group.KeepShortestPath();
        }

        RefreshSummary();
    }

    private void OnKeepLargestFileClicked(object sender, RoutedEventArgs e)
    {
        foreach (var group in Groups)
        {
            group.KeepLargestFile();
        }

        RefreshSummary();
    }

    private void OnMarkExtrasClicked(object sender, RoutedEventArgs e)
    {
        foreach (var group in Groups)
        {
            foreach (var item in group.Items)
            {
                item.IsSelected = !item.IsKeep;
            }
        }

        RefreshSummary();
    }

    private void OnMoveMarkedClicked(object sender, RoutedEventArgs e)
    {
        var marked = Groups.SelectMany(group => group.GetMarkedItems()).ToArray();
        if (marked.Length == 0)
        {
            System.Windows.MessageBox.Show("Сначала отметьте файлы для карантина.", "Карантин", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"Перенести в карантин {marked.Length} файлов?\n\nОни не будут удалены. Их можно проверить в папке карантина.",
            "Карантин дубликатов",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        var batchRoot = Path.Combine(QuarantineRoot, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(batchRoot);
        var movedItems = new List<DuplicateItemViewModel>();
        foreach (var item in marked)
        {
            try
            {
                var relative = item.Photo.Path
                    .Replace(Path.GetPathRoot(item.Photo.Path) ?? "", "", StringComparison.OrdinalIgnoreCase)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var target = Path.Combine(batchRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    target = Path.Combine(Path.GetDirectoryName(target)!, $"{Path.GetFileNameWithoutExtension(target)}_{Guid.NewGuid():N}{Path.GetExtension(target)}");
                }

                File.Move(item.Photo.Path, target);
                movedItems.Add(item);
            }
            catch
            {
            }
        }

        RemoveMovedItems(movedItems);
        System.Windows.MessageBox.Show($"Перенесено в карантин: {movedItems.Count}\n\nПапка: {batchRoot}", "Карантин", MessageBoxButton.OK, MessageBoxImage.Information);
        RefreshSummary();
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
