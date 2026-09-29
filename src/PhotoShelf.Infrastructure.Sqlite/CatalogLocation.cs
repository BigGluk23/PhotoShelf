namespace PhotoShelf.Infrastructure.Sqlite;

/// <summary>Startup explicitly selects and pins storage before a production store can open it.</summary>
public sealed class CatalogLocation
{
    private readonly object _gate = new();
    private string? _directory;
    private CatalogStorageSelection? _selection;
    public bool IsIsolatedSmoke { get; private set; }
    public string LocalDirectory { get; }
    public string LegacyDirectory { get; }
    public CatalogStorageSelection? Selection { get { lock (_gate) return _selection; } }
    public CatalogLocation() : this(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoShelf"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoShelf")) { }

    public CatalogLocation(string localDirectory, string legacyDirectory)
    {
        LocalDirectory = Path.GetFullPath(localDirectory);
        LegacyDirectory = Path.GetFullPath(legacyDirectory);
    }

    public string DirectoryPath
    {
        get
        {
            lock (_gate) return _directory ?? throw new InvalidOperationException(
                "Catalog storage must be selected and prepared before opening the catalog.");
        }
    }

    // Diagnostics/cache are allowed before startup has selected a catalog.
    public string DerivedDataDirectory { get { lock (_gate) return IsIsolatedSmoke ? _directory! : LocalDirectory; } }
    public bool UsesLegacyStorage => !IsIsolatedSmoke && Selection?.DirectoryPath is { } selected &&
        CatalogStorageFiles.PathsEqual(selected, LegacyDirectory) && !CatalogStorageFiles.PathsEqual(LocalDirectory, LegacyDirectory);

    /// <summary>Read-only discovery. Errors and two populated roots require an explicit startup decision.</summary>
    public CatalogStorageDiscovery Discover()
    {
        lock (_gate)
        {
            if (IsIsolatedSmoke) throw new InvalidOperationException("Smoke storage is already isolated.");
            var pointer = Path.Combine(LocalDirectory, CatalogStorageFiles.SelectionFileName);
            if (CatalogStorageFiles.Exists(pointer))
                return new(CatalogStorageFiles.ReadSelection(LocalDirectory), []);
            var generations = Path.Combine(LocalDirectory, CatalogStorageFiles.GenerationDirectoryName);
            if (CatalogStorageFiles.Exists(generations))
            {
                CatalogStorageFiles.RejectReparse(generations);
                foreach (var generation in Directory.EnumerateDirectories(generations))
                {
                    CatalogStorageFiles.RejectReparse(generation);
                    var manifest = Path.Combine(generation, CatalogStorageFiles.GenerationFileName);
                    if (CatalogStorageFiles.Exists(manifest))
                        throw new InvalidDataException($"A completed catalog generation exists at {generation}, but its storage selector is missing at {pointer}. " +
                            "Restore the storage selection explicitly; no retained source or new empty catalog was opened.");
                }
            }
            var paths = new[] { LocalDirectory, LegacyDirectory }.Distinct(CatalogStorageFiles.PathComparer);
            return new(null, paths.Select(CatalogStorageFiles.InspectCandidate).ToArray());
        }
    }

    public void PinExisting(CatalogStorageSelection selection)
    {
        lock (_gate)
        {
            EnsureUnpinned();
            var current = CatalogStorageFiles.ReadSelection(LocalDirectory);
            if (current != selection) throw new IOException("The catalog storage selection changed during startup.");
            _selection = current;
            _directory = current.DirectoryPath;
        }
    }

    /// <summary>Call off the dispatcher, while the application startup barrier is still held.</summary>
    public void CommitPrepared(PreparedCatalogGeneration prepared)
    {
        lock (_gate)
        {
            EnsureUnpinned();
            if (!ReferenceEquals(prepared.Location, this)) throw new ArgumentException("The generation belongs to another storage selector.", nameof(prepared));
            var selection = prepared.Publish();
            _selection = selection;
            _directory = selection.DirectoryPath;
        }
    }

    private void EnsureUnpinned()
    {
        if (_directory is not null) throw new InvalidOperationException("The catalog location has already been pinned.");
    }

    public string CreateIsolatedSmokeDirectory()
    {
        lock (_gate)
        {
            EnsureUnpinned();
            // CreateTempSubdirectory creates a new directory atomically; no existing catalog can be selected.
            _directory = Directory.CreateTempSubdirectory("PhotoShelf-ui-smoke-" + Guid.NewGuid().ToString("N") + "-").FullName;
            IsIsolatedSmoke = true;
            return _directory;
        }
    }
}
