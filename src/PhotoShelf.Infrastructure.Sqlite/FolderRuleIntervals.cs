using System.Text;

namespace PhotoShelf.Infrastructure.Sqlite;

/// <summary>Compiles longest-folder overrides into disjoint SQLite BINARY path-key ranges.</summary>
internal sealed record FolderRuleIntervals(IReadOnlyList<string[]> Bounded, string Tail)
{
    internal static FolderRuleIntervals Compile(IEnumerable<string> included, IEnumerable<string> excluded,
        CancellationToken token)
    {
        var rules = new Dictionary<string, bool>(StringComparer.Ordinal);
        void Add(IEnumerable<string> folders, bool value)
        {
            foreach (var folder in folders)
            {
                token.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(folder)) rules[SqliteDesktopCatalogStore.NormalizePathKey(folder)] = value;
            }
        }
        Add(included, true);
        Add(excluded, false); // Equal normalized paths exclude, including duplicate/conflicting saved settings.

        var boundaries = new List<Boundary>(rules.Count * 2);
        var id = 0;
        foreach (var (folder, value) in rules)
        {
            token.ThrowIfCancellationRequested();
            var rule = new Rule(id++, folder.Length, value);
            boundaries.Add(new Boundary(folder + "/", rule, true));
            boundaries.Add(new Boundary(folder + "0", rule, false));
        }
        // UTF-16 ordinal ordering differs from SQLite's UTF-8 BINARY order for non-BMP filenames.
        boundaries.Sort((left, right) => Compare(left, right));
        token.ThrowIfCancellationRequested();
        var active = new SortedSet<Rule>(Comparer<Rule>.Create((left, right) =>
        {
            var depth = left.Depth.CompareTo(right.Depth);
            return depth != 0 ? depth : left.Id.CompareTo(right.Id);
        }));
        var bounded = new List<string[]>();
        var includedNow = true;
        var start = ""; // All valid catalog paths are nonempty; the final interval has no upper bound.
        for (var index = 0; index < boundaries.Count;)
        {
            token.ThrowIfCancellationRequested();
            var boundary = boundaries[index];
            do
            {
                var change = boundaries[index++];
                if (change.Enter) active.Add(change.Rule); else active.Remove(change.Rule);
            } while (index < boundaries.Count && Compare(boundary, boundaries[index]) == 0);
            var includedNext = active.Count == 0 || active.Max!.Included;
            if (includedNow && !includedNext) bounded.Add([start, boundary.Path]);
            else if (!includedNow && includedNext) start = boundary.Path;
            includedNow = includedNext;
        }
        // Every folder interval is finite, so the default included state always resumes at the end.
        return new FolderRuleIntervals(bounded, start);
    }

    private static int Compare(Boundary left, Boundary right) => left.Bytes.AsSpan().SequenceCompareTo(right.Bytes);
    private sealed record Rule(int Id, int Depth, bool Included);
    private sealed record Boundary(string Path, Rule Rule, bool Enter)
    {
        internal byte[] Bytes { get; } = Encoding.UTF8.GetBytes(Path);
    }
}
