namespace PhotoShelf.Application.Catalog;

/// <summary>Pure path selection; never enumerates or opens a directory.</summary>
public static class LibraryFolderScope
{
    public static IReadOnlyList<string> ReconciliationTargets(IEnumerable<string> requestedFolders,
        IReadOnlyList<string> libraryRoots)
    {
        static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var requested = requestedFolders.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in libraryRoots.Select(Normalize))
            foreach (var path in requested)
            {
                if (Contains(path, root, recursive: true)) candidates.Add(path);
                else if (Contains(root, path, recursive: true)) candidates.Add(root);
            }
        // A request for an excluded drive can still include explicit child roots.
        // Intersect with library scope, never the independent unchecked browse scope.
        var targets = new List<string>();
        foreach (var path in candidates.OrderBy(path => path.Length).ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            if (!targets.Any(parent => Contains(path, parent, recursive: true))) targets.Add(path);
        return targets;
    }

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
