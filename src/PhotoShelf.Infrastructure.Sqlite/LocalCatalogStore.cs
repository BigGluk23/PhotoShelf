using System.Text.Json;
using PhotoShelf.Application.Catalog;
namespace PhotoShelf.Infrastructure.Sqlite;

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
        File.Move(tempPath, CatalogPath, overwrite: true);
    }
}

