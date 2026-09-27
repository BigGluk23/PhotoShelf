using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

namespace PhotoShelf.Desktop;

public sealed class FolderNode : INotifyPropertyChanged
{
    private bool _isIncluded = true;
    private bool _isExpanded;
    private int _directItemCount;

    public FolderNode(string fullPath)
    {
        FullPath = fullPath;
        Name = GetDisplayName(fullPath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FullPath { get; }

    public string Name { get; }

    public ObservableCollection<FolderNode> Children { get; } = new();

    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (_isIncluded == value)
            {
                return;
            }

            _isIncluded = value;
            OnPropertyChanged(nameof(IsIncluded));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    public int DirectItemCount
    {
        get => _directItemCount;
        set
        {
            if (_directItemCount == value)
            {
                return;
            }

            _directItemCount = value;
            OnPropertyChanged(nameof(DirectItemCount));
            OnPropertyChanged(nameof(DisplayText));
        }
    }

    public string DisplayText => DirectItemCount > 0 ? $"{Name}  ({DirectItemCount})" : Name;

    private static string GetDisplayName(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
