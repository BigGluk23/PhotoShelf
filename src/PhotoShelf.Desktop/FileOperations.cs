using System.IO;
using PhotoShelf.Application.Files;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Desktop;

internal static class FileOperations
{
    internal static FileMoveService CreateService()
    {
        var selection = LocalCatalogStore.StorageLocation.Selection;
        return selection is null ? new() : new(new FileMoveOptions
        {
            JournalRelocation = new JournalPathRelocation(
                Path.Combine(selection.SourceDirectory ?? selection.DirectoryPath, "operations"),
                Path.Combine(selection.DirectoryPath, "operations"))
        });
    }
}
