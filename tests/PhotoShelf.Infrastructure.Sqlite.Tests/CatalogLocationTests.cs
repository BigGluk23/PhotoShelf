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
        var root = Directory.CreateTempSubdirectory("photoshelf-location-test-").FullName;
        var location = new CatalogLocation(Path.Combine(root, "local"), Path.Combine(root, "roaming"));
        var production = location.DirectoryPath;
        Assert.Equal(Path.Combine(root, "local"), production);
        Assert.Throws<InvalidOperationException>(() => location.CreateIsolatedSmokeDirectory());
        Assert.False(location.IsIsolatedSmoke);
        Assert.Equal(production, location.DirectoryPath);
        Directory.Delete(root);
    }

    [Fact]
    public void ExistingRoamingCatalogAndJournalsStayTogetherWhileDerivedDataUsesLocalStorage()
    {
        var root = Directory.CreateTempSubdirectory("photoshelf-location-test-").FullName;
        try
        {
            var local = Path.Combine(root, "local"); var roaming = Path.Combine(root, "roaming");
            Directory.CreateDirectory(roaming);
            var journal = Path.Combine(roaming, "operation.jsonl"); File.WriteAllText(journal, "must survive");
            var location = new CatalogLocation(local, roaming);
            Assert.Equal(roaming, location.DirectoryPath);
            Assert.True(location.UsesLegacyStorage);
            Assert.Equal(local, location.DerivedDataDirectory);
            Assert.False(Directory.Exists(local));
            Assert.Equal("must survive", File.ReadAllText(journal));
            File.Delete(journal); Directory.Delete(roaming);
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public void DerivedDataAndCatalogShareTheOwnedSmokeRoot()
    {
        var location = new CatalogLocation();
        var root = location.CreateIsolatedSmokeDirectory();
        try { Assert.Equal(root, location.DirectoryPath); Assert.Equal(root, location.DerivedDataDirectory); Assert.False(location.UsesLegacyStorage); }
        finally { Directory.Delete(root); }
    }
}
