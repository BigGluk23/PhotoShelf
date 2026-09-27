using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class CatalogLocationTests
{
    [Fact]
    public void SmokeRootsAreNewEmptyUniqueAndPinned()
    {
        var first = new CatalogLocation();
        var second = new CatalogLocation();
        var a = first.CreateIsolatedSmokeDirectory();
        var b = second.CreateIsolatedSmokeDirectory();
        try
        {
            Assert.NotEqual(a, b);
            Assert.True(Path.IsPathFullyQualified(a));
            Assert.StartsWith("PhotoShelf-ui-smoke-", Path.GetFileName(a));
            Assert.Empty(Directory.EnumerateFileSystemEntries(a));
            Assert.Empty(Directory.EnumerateFileSystemEntries(b));
            Assert.True(first.IsIsolatedSmoke);
            Assert.Equal(a, first.DirectoryPath);
            Assert.Throws<InvalidOperationException>(() => first.CreateIsolatedSmokeDirectory());
            Assert.Equal(a, first.DirectoryPath);
        }
        finally
        {
            Directory.Delete(a); // These fixtures remain empty; never recursive application-data cleanup.
            Directory.Delete(b);
        }
    }

    [Fact]
    public void SmokeCannotRedirectAnAlreadyUsedProductionCatalog()
    {
        var location = new CatalogLocation();
        var production = location.DirectoryPath;
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoShelf"), production);
        Assert.Throws<InvalidOperationException>(() => location.CreateIsolatedSmokeDirectory());
        Assert.False(location.IsIsolatedSmoke);
        Assert.Equal(production, location.DirectoryPath);
    }
}
