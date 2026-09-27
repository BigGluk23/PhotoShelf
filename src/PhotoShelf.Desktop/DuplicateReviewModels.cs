using System.Collections.ObjectModel;
using System.ComponentModel;

namespace PhotoShelf.Desktop;

public sealed class DuplicateGroupViewModel : INotifyPropertyChanged
{
    private DuplicateItemViewModel? _keepItem;

    public DuplicateGroupViewModel(DuplicateGroup group, int index)
    {
        Index = index;
        SizeBytes = group.SizeBytes;
        Hash = group.Hash;
        Items = new ObservableCollection<DuplicateItemViewModel>(
            group.Items.Select(item => new DuplicateItemViewModel(item, this)));
        KeepItem = Items
            .OrderByDescending(static item => item.Photo.FileModifiedAt ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }

    public long SizeBytes { get; }

    public string Hash { get; }

    public ObservableCollection<DuplicateItemViewModel> Items { get; }

    public DuplicateItemViewModel? KeepItem
    {
        get => _keepItem;
        set
        {
            if (ReferenceEquals(_keepItem, value))
            {
                return;
            }

            _keepItem = value;
            foreach (var item in Items)
            {
                item.NotifyRoleChanged();
            }

            OnPropertyChanged(nameof(KeepItem));
            OnPropertyChanged(nameof(KeepPath));
            OnPropertyChanged(nameof(MoveCount));
            OnPropertyChanged(nameof(Summary));
        }
    }

    public string Summary => $"Группа {Index}: {Items.Count} файлов, {SizeBytes / 1024d / 1024d:0.0} MB";

    public string KeepPath => KeepItem?.Photo.Path ?? "";

    public int MoveCount => Items.Count(item => item.IsMarkedForMove);

    public void Keep(DuplicateItemViewModel item)
    {
        KeepItem = item;
    }

    public void KeepNewest()
    {
        KeepItem = Items
            .OrderByDescending(static item => item.Photo.FileModifiedAt ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    public void KeepShortestPath()
    {
        KeepItem = Items
            .OrderBy(static item => item.Photo.Path.Length)
            .ThenBy(static item => item.Photo.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public void KeepLargestFile()
    {
        KeepItem = Items
            .OrderByDescending(static item => item.Photo.FileSizeBytes)
            .ThenByDescending(static item => item.Photo.FileModifiedAt ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    public IReadOnlyList<DuplicateItemViewModel> GetMarkedItems()
    {
        return Items.Where(item => item.IsMarkedForMove).ToArray();
    }

    internal void NotifyMoveCountChanged()
    {
        OnPropertyChanged(nameof(MoveCount));
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class DuplicateItemViewModel : INotifyPropertyChanged
{
    private readonly DuplicateGroupViewModel _group;
    private bool _isSelected;

    public DuplicateItemViewModel(PhotoItem photo, DuplicateGroupViewModel group)
    {
        Photo = photo;
        _group = group;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PhotoItem Photo { get; }

    public bool IsKeep => ReferenceEquals(_group.KeepItem, this);

    public bool IsMarkedForMove => !IsKeep && IsSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(IsMarkedForMove));
            OnPropertyChanged(nameof(RoleText));
            _group.NotifyMoveCountChanged();
        }
    }

    public string RoleText => IsKeep ? "Оставить" : IsSelected ? "В карантин" : "Не выбрано";

    public void NotifyRoleChanged()
    {
        OnPropertyChanged(nameof(IsKeep));
        OnPropertyChanged(nameof(IsMarkedForMove));
        OnPropertyChanged(nameof(RoleText));
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
