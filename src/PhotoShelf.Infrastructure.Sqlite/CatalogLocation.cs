namespace PhotoShelf.Infrastructure.Sqlite;

/// <summary>A process pins its catalog before any store can open it. Smoke mode owns a fresh temporary root.</summary>
public sealed class CatalogLocation
{
    private readonly object _gate = new();
    private string? _directory;
    public bool IsIsolatedSmoke { get; private set; }

    public string DirectoryPath
    {
        get
        {
            lock (_gate)
                return _directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoShelf");
        }
    }

    public string CreateIsolatedSmokeDirectory()
    {
        lock (_gate)
        {
            if (_directory is not null) throw new InvalidOperationException("The catalog location has already been used.");
            // CreateTempSubdirectory creates a new directory atomically; no existing catalog can be selected.
            _directory = Directory.CreateTempSubdirectory("PhotoShelf-ui-smoke-" + Guid.NewGuid().ToString("N") + "-").FullName;
            IsIsolatedSmoke = true;
            return _directory;
        }
    }
}
