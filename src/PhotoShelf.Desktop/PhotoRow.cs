using System.Collections.ObjectModel;
using System.ComponentModel;

namespace PhotoShelf.Desktop;

public sealed class PhotoRow : INotifyPropertyChanged
{
    private bool _isCollapsed;

    private PhotoRow(string header, string groupKey, bool isCollapsed)
    {
        Header = header;
        GroupKey = groupKey;
        _isCollapsed = isCollapsed;
        Items = new ObservableCollection<PhotoItem>();
    }

    public PhotoRow(IEnumerable<PhotoItem> items)
    {
        Items = new ObservableCollection<PhotoItem>(items);
    }

    public string? Header { get; }

    public string? GroupKey { get; }

    public bool IsHeader => Header is not null;

    public bool IsPhotoRow => Header is null;

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (_isCollapsed == value)
            {
                return;
            }

            _isCollapsed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCollapsed)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedGlyph)));
        }
    }

    public string ExpandedGlyph => IsCollapsed ? "▸" : "▾";

    public ObservableCollection<PhotoItem> Items { get; }

    public double MinimumHeight { get; init; }

    public void SetItems(IEnumerable<PhotoItem> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static PhotoRow CreateHeader(string header, string groupKey, bool isCollapsed) => new(header, groupKey, isCollapsed);
}
