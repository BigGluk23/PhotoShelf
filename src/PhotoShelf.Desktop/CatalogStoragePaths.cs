using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Desktop;

internal static class CatalogStoragePaths
{
    // Keep retained sources and incomplete generations out of scanning and quarantine too.
    internal static string[] InternalRoots => LocalCatalogStore.IsIsolatedSmokeCatalog
        ? [LocalCatalogStore.CatalogDirectory]
        : [LocalCatalogStore.CatalogDirectory, LocalCatalogStore.DerivedDataDirectory, LocalCatalogStore.StorageLocation.LegacyDirectory];
}
