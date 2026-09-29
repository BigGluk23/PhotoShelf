using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PhotoShelf.Application.Catalog;
namespace PhotoShelf.Infrastructure.Sqlite;

public static class LocalCatalogStore
{
    private static readonly CatalogLocation Location = new();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public static string CatalogDirectory => Location.DirectoryPath;
    public static CatalogLocation StorageLocation => Location;
    public static string DerivedDataDirectory => Location.DerivedDataDirectory;
    public static bool UsesLegacyStorage => Location.UsesLegacyStorage;

    public static bool IsIsolatedSmokeCatalog => Location.IsIsolatedSmoke;

    public static string CreateIsolatedSmokeCatalog() => Location.CreateIsolatedSmokeDirectory();

    public static string CatalogPath => Path.Combine(CatalogDirectory, "catalog-v1.json");

    public static LocalCatalogState Load() => ReadLegacyAsync(CatalogPath).GetAwaiter().GetResult() ?? new LocalCatalogState();

    /// <summary>Only a missing file is empty. An unreadable or malformed legacy catalog stops import.</summary>
    public static async Task<LocalCatalogState?> ReadLegacyAsync(string path, CancellationToken token = default)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        await using (stream)
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(info =>
            {
                if (info.Type != typeof(LocalCatalogState)) return;
                foreach (var property in info.Properties)
                    if (property.Name is nameof(LocalCatalogState.Version) or nameof(LocalCatalogState.Items)) property.IsRequired = true;
            });
            LocalCatalogState state;
            try
            {
                state = await JsonSerializer.DeserializeAsync<LocalCatalogState>(stream,
                    new JsonSerializerOptions { TypeInfoResolver = resolver }, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Legacy catalog is null; import was not started.");
            }
            catch (JsonException exception)
            { throw new InvalidDataException("Legacy catalog JSON is invalid; it was preserved and import was not started.", exception); }
            if (state.Version != 1) throw new InvalidDataException($"Unsupported legacy catalog version {state.Version}; import was not started.");
            if (state.Items is null || state.DuplicateHashes is null || state.ExpandedFolders is null || state.ExcludedFolders is null
                || state.IncludedFolders is null || state.WatchedFolders is null || state.QuarantineBatchDirectories is null
                || state.DateGroupingMode is null || state.ViewMode is null)
                throw new InvalidDataException("Legacy catalog contains null collections or settings; import was not started.");
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in state.Items)
            {
                token.ThrowIfCancellationRequested();
                if (item is null || string.IsNullOrWhiteSpace(item.Path) || item.SizeBytes < 0
                    || !paths.Add(SqliteDesktopCatalogStore.NormalizePathKey(item.Path)))
                    throw new InvalidDataException("Legacy catalog contains invalid or ambiguous media paths; import was not started.");
            }
            if (!double.IsFinite(state.TileWidth) || state.ExpandedFolders.Concat(state.ExcludedFolders).Concat(state.IncludedFolders)
                .Concat(state.WatchedFolders).Concat(state.QuarantineBatchDirectories).Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Legacy catalog contains invalid settings; import was not started.");
            return state;
        }
    }

    public static void Save(LocalCatalogState state)
    {
        Directory.CreateDirectory(CatalogDirectory);
        var tempPath = CatalogPath + ".tmp";
        var json = JsonSerializer.Serialize(state, SerializerOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, CatalogPath, overwrite: true);
    }
}
