namespace PhotoShelf.Infrastructure.Sqlite;

/// <summary>A process pins its catalog before any store can open it. Smoke mode owns a fresh temporary root.</summary>
public sealed class CatalogLocation
{
    private readonly object _gate = new();
    private readonly string _localDirectory;
    private readonly string _legacyDirectory;
    private string? _directory;
    public bool IsIsolatedSmoke { get; private set; }
    public CatalogLocation() : this(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoShelf"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoShelf")) { }

    public CatalogLocation(string localDirectory, string legacyDirectory)
    {
        _localDirectory = Path.GetFullPath(localDirectory);
        _legacyDirectory = Path.GetFullPath(legacyDirectory);
    }

    public string DirectoryPath
    {
        get
        {
            lock (_gate)
                // Keep an existing catalog and its recovery journals together. Never silently start an empty replacement.
                return _directory ??= Directory.Exists(_legacyDirectory) ? _legacyDirectory : _localDirectory;
        }
    }

    public string DerivedDataDirectory { get { lock (_gate) { _ = DirectoryPath; return IsIsolatedSmoke ? _directory! : _localDirectory; } } }
    public bool UsesLegacyStorage => !IsIsolatedSmoke &&
        DirectoryPath.Equals(_legacyDirectory, StringComparison.OrdinalIgnoreCase) &&
        !_legacyDirectory.Equals(_localDirectory, StringComparison.OrdinalIgnoreCase);

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
