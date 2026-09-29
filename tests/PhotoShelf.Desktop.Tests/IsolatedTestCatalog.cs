using System.Runtime.CompilerServices;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Desktop.Tests;

internal static class IsolatedTestCatalog
{
    // MainWindow and preview caches construct default stores. Pin their location before
    // any test can touch those types; no test may fall back to the user's real catalog.
    // Leave this owned TEMP directory available for diagnostics if the process fails.
    internal static string DirectoryPath { get; private set; } = "";

    [ModuleInitializer]
    internal static void Initialize() => DirectoryPath = LocalCatalogStore.CreateIsolatedSmokeCatalog();
}
