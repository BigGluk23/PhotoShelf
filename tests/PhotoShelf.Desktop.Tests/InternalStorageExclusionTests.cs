using System.IO;
using PhotoShelf.Infrastructure.Sqlite;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

public sealed class InternalStorageExclusionTests
{
    private static bool IsInternalStoragePath(string path, params string[] roots) =>
        (bool)typeof(PhotoScanner).GetMethod("IsInternalStoragePath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [path, roots])!;

    [Fact]
    public void LegacyCatalogAndSeparateLocalThumbnailCacheAreBothExcludedWithoutExcludingSiblings()
    {
        var root = Directory.CreateTempSubdirectory("photoshelf-storage-exclusion-").FullName;
        try
        {
            var local = Path.Combine(root, "Local", "PhotoShelf");
            var legacy = Directory.CreateDirectory(Path.Combine(root, "Roaming", "PhotoShelf")).FullName;
            var location = new CatalogLocation(local, legacy);
            Assert.False(location.UsesLegacyStorage);
            Assert.True(IsInternalStoragePath(Path.Combine(legacy, "catalog.sqlite"), legacy, location.LocalDirectory, Path.Combine(location.LocalDirectory, "storage-generations", "incomplete")));
            Assert.True(IsInternalStoragePath(Path.Combine(local, "thumb-cache-v2", "ps-thumb-v2-own.png"), legacy, location.LocalDirectory, Path.Combine(location.LocalDirectory, "storage-generations", "incomplete")));
            Assert.True(IsInternalStoragePath(local, legacy, location.LocalDirectory, Path.Combine(location.LocalDirectory, "storage-generations", "incomplete")));
            Assert.False(IsInternalStoragePath(local + "-family" + Path.DirectorySeparatorChar + "photo.png", legacy, location.LocalDirectory, Path.Combine(location.LocalDirectory, "storage-generations", "incomplete")));
            Assert.False(IsInternalStoragePath(legacy + "-family" + Path.DirectorySeparatorChar + "photo.png", legacy, location.LocalDirectory, Path.Combine(location.LocalDirectory, "storage-generations", "incomplete")));
            Assert.False(Directory.Exists(local)); // The exclusion performs no creation or file writes.
        }
        finally { Directory.Delete(root, true); }
    }
}
