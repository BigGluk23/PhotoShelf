using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using PhotoShelf.Application.Catalog;

namespace PhotoShelf.Desktop;

public sealed class FolderNode : INotifyPropertyChanged
{
    private bool _isIncluded = true;
    private bool? _checkState = true;
    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set { _isVisible = value; OnPropertyChanged(nameof(IsVisible)); } }
    private bool _isExpanded;
    private int _directItemCount;

    public FolderNode(string fullPath, FolderNode? parent = null)
    {
        FullPath = fullPath;
        Parent = parent;
        Name = GetDisplayName(fullPath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsPlaceholder { get; private set; }
    public bool IsLoading { get; set; }
    public bool ChildrenLoaded { get; set; }
    public static FolderNode Placeholder() => new FolderNode("…") { IsPlaceholder = true };
    public string FullPath { get; }
    public FolderNode? Parent { get; }
    public bool? CheckState => _checkState;

    public void ApplyInclusion(FolderInclusionRules rules) => ApplyInclusion(rules.IsIncluded(FullPath), rules.GetCheckState(FullPath));
    public void ApplyInclusion(bool included, bool? checkState)
    {
        IsIncluded = included;
        _checkState = checkState;
        // Always restore the one-way binding after a CheckBox internally toggles its target.
        OnPropertyChanged(nameof(CheckState));
    }
    public bool ToggleInclusion(FolderInclusionRules rules)
    {
        var included = CheckState != true;
        rules.SetIncluded(FullPath, included);
        for (FolderNode? node = this; node is not null; node = node.Parent) node.ApplyInclusion(rules);
        return included;
    }

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
