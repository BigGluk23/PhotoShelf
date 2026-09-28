namespace PhotoShelf.Application.Catalog;

/// <summary>In-memory folder selection. Accessing or changing rules never reads the filesystem.</summary>
public sealed class FolderInclusionRules
{
    private readonly Dictionary<string, bool> _rules = new(StringComparer.OrdinalIgnoreCase);
    public FolderInclusionRules(IEnumerable<string> excludedFolders, IEnumerable<string>? includedFolders = null)
    {
        foreach (var path in includedFolders ?? []) _rules[Normalize(path)] = true;
        // Conflicting legacy/imported settings must fail closed for the exact same path.
        foreach (var path in excludedFolders) _rules[Normalize(path)] = false;
    }

    public IReadOnlyList<string> ExcludedFolders => _rules.Where(pair => !pair.Value).Select(pair => pair.Key).ToArray();
    public IReadOnlyList<string> IncludedFolders => _rules.Where(pair => pair.Value).Select(pair => pair.Key).ToArray();
    public FolderInclusionRules Snapshot() => new(ExcludedFolders, IncludedFolders);
    public bool IsIncluded(string path)
    {
        for (string? current = Normalize(path); current is not null; current = Parent(current))
            if (_rules.TryGetValue(current, out var included)) return included;
        return true; // Preserve existing installations: only explicit exclusions opt out.
    }
    public bool? GetCheckState(string folder)
    {
        folder = Normalize(folder);
        var included = IsIncluded(folder);
        // Persisted descendants matter even when no child nodes have been expanded/loaded.
        return _rules.Any(pair => pair.Value != included && IsUnder(pair.Key, folder)) ? null : included;
    }
    public bool MayContainIncluded(string folder) => GetCheckState(folder) != false;
    public void SetIncluded(string path, bool included)
    {
        path = Normalize(path);
        // A direct click applies to this subtree. Never remove an ancestor's rule: an
        // included child under an excluded drive is an exception, not an included drive.
        foreach (var key in _rules.Keys.Where(key => IsUnder(key, path)).ToArray()) _rules.Remove(key);
        if (IsIncluded(path) != included) _rules[path] = included;
    }
    private static string? Parent(string path) => Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)
            ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
    private static bool IsUnder(string path, string folder) => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
