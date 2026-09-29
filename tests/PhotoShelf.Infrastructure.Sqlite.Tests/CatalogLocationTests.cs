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
    public async Task SmokeCannotRedirectAnAlreadyPinnedProductionCatalog()
    {
        var root = Directory.CreateTempSubdirectory("photoshelf-location-test-").FullName;
        var location = new CatalogLocation(Path.Combine(root, "local"), Path.Combine(root, "roaming"));
        try
        {
            using var prepared = await new CatalogGenerationMigration().PrepareAsync(location, null);
            await new SqliteDesktopCatalogStore(prepared.DirectoryPath).InitializeAsync();
            location.CommitPrepared(prepared);
            Assert.Throws<InvalidOperationException>(() => location.CreateIsolatedSmokeDirectory());
            Assert.False(location.IsIsolatedSmoke);
            Assert.Equal(prepared.DirectoryPath, location.DirectoryPath);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public void ExistingRoamingEvidenceIsDiscoveredWithoutPinningOrWriting()
    {
        var root = Directory.CreateTempSubdirectory("photoshelf-location-test-").FullName;
        try
        {
            var local = Path.Combine(root, "local"); var roaming = Path.Combine(root, "roaming");
            Directory.CreateDirectory(roaming);
            var operations = Directory.CreateDirectory(Path.Combine(roaming, "operations")).FullName;
            var journal = Path.Combine(operations, "operation.jsonl"); File.WriteAllText(journal, "must survive");
            var location = new CatalogLocation(local, roaming);
            var source = Assert.Single(location.Discover().Candidates, candidate => candidate.HasEvidence);
            Assert.Equal(roaming, source.DirectoryPath);
            Assert.True(source.HasOperationEvidence);
            Assert.Throws<InvalidOperationException>(() => location.DirectoryPath);
            Assert.Equal(local, location.DerivedDataDirectory);
            Assert.False(Directory.Exists(local));
            Assert.Equal("must survive", File.ReadAllText(journal));
            File.Delete(journal); Directory.Delete(operations); Directory.Delete(roaming);
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
