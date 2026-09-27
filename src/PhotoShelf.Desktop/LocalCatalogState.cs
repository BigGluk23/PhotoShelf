using System.IO;
using System.Text.Json;

namespace PhotoShelf.Desktop;

public sealed class LocalCatalogState
{
    public int Version { get; set; } = 1;

    public double TileWidth { get; set; } = 178;

    public bool ShowVideos { get; set; } = true;

    public bool IncludeSystemFolders { get; set; }

    public string DateGroupingMode { get; set; } = "FileDate";

    public List<SavedMediaItem> Items { get; set; } = new();

    public List<string> ExcludedFolders { get; set; } = new();

    public List<SavedDuplicateHash> DuplicateHashes { get; set; } = new();
}

public sealed class SavedMediaItem
{
    public string Path { get; set; } = string.Empty;

    public bool IsFavorite { get; set; }
}

public sealed class SavedDuplicateHash
{
    public string Path { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTime? FileModifiedAt { get; set; }

    public string Hash { get; set; } = string.Empty;
}

public static class LocalCatalogStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public static string CatalogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoShelf");

    public static string CatalogPath => Path.Combine(CatalogDirectory, "catalog-v1.json");

    public static LocalCatalogState Load()
    {
        try
        {
            if (!File.Exists(CatalogPath))
            {
                return new LocalCatalogState();
            }

            var json = File.ReadAllText(CatalogPath);
            return JsonSerializer.Deserialize<LocalCatalogState>(json, SerializerOptions) ?? new LocalCatalogState();
        }
        catch
        {
            return new LocalCatalogState();
        }
    }

    public static void Save(LocalCatalogState state)
    {
        Directory.CreateDirectory(CatalogDirectory);
        var tempPath = CatalogPath + ".tmp";
        var json = JsonSerializer.Serialize(state, SerializerOptions);
        File.WriteAllText(tempPath, json);
        File.Copy(tempPath, CatalogPath, overwrite: true);
        File.Delete(tempPath);
    }
}

public sealed record DuplicateGroup(long SizeBytes, string Hash, IReadOnlyList<PhotoItem> Items);
