namespace PhotoShelf.Infrastructure.Sqlite;

public sealed record SqliteCatalogOptions(string DatabasePath)
{
    public string GetValidatedFullPath()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new ArgumentException("A catalog database path is required.", nameof(DatabasePath));
        }

        return Path.GetFullPath(DatabasePath);
    }
}

