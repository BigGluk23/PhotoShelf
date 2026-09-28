namespace PhotoShelf.Application.Catalog;

/// <summary>Pure path selection; never enumerates or opens a directory.</summary>
public static class LibraryFolderScope
{
    public static bool Contains(string path, string folder, bool recursive) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        (recursive
            ? path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            : string.Equals(Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> IncludedRoots(IEnumerable<string> watchedFolders, FolderInclusionRules rules)
    {
        // An excluded drive with an included child is watched from that child, not
        // from the whole drive. Ancestor discovery cannot re-enable excluded branches.
        var candidates = watchedFolders.Concat(rules.IncludedFolders)
            .Where(rules.IsIncluded).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length).ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in candidates)
            if (!result.Any(parent => Contains(path, parent, recursive: true))) result.Add(path);
        return result;
    }
}
