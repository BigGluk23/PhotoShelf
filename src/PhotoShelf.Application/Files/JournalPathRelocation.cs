namespace PhotoShelf.Application.Files;

/// <summary>Redirects copied journal links without changing checksummed journal bytes.</summary>
public sealed class JournalPathRelocation(string sourceOperations, string currentOperations)
{
    private readonly string _source = Path.GetFullPath(sourceOperations).TrimEnd(Path.DirectorySeparatorChar);
    private readonly string _current = Path.GetFullPath(currentOperations).TrimEnd(Path.DirectorySeparatorChar);

    public string Resolve(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Журнал должен иметь абсолютный путь");
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (!full.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
            !(string.Equals(parent, _source, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(parent, _current, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Ссылка на журнал вне выбранного каталога; автоматическое восстановление остановлено");
        var result = Path.Combine(_current, Path.GetFileName(full));
        LegacyJournalReviewService.RejectLinks(result);
        return result;
    }
}
