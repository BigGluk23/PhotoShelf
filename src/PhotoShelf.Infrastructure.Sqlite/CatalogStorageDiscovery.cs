using System.Text.Json;

namespace PhotoShelf.Infrastructure.Sqlite;

public sealed record CatalogStorageSelection(int Version, string StorageId, string DirectoryPath, string? SourceDirectory);

public sealed record CatalogStorageCandidate(string DirectoryPath, bool HasDatabase, bool HasLegacyJson,
    bool HasOperationEvidence, string? Error)
{
    public bool HasEvidence => HasDatabase || HasLegacyJson || HasOperationEvidence;
    public bool IsAvailable => Error is null;
}

public sealed record CatalogStorageDiscovery(CatalogStorageSelection? Selection, IReadOnlyList<CatalogStorageCandidate> Candidates);

internal static class CatalogStorageFiles
{
    internal const string SelectionFileName = "storage-selection-v1.json";
    internal const string GenerationFileName = "storage-generation-v1.json";
    internal const string GenerationDirectoryName = "storage-generations";
    internal const string DatabaseFileName = "catalog-v2.sqlite";
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static bool PathsEqual(string a, string b) => PathComparer.Equals(Path.GetFullPath(a), Path.GetFullPath(b));

    // File.Exists/Directory.Exists hide access and other I/O failures, which must never mean an empty catalog.
    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Catalog storage contains a link/reparse point: {path}");
    }

    internal static CatalogStorageCandidate InspectCandidate(string directory)
    {
        var database = false; var json = false; var operations = false;
        string? backups = null;
        try
        {
            if (!Exists(directory)) return new(directory, false, false, false, null);
            RejectReparse(directory);
            // Enumeration proves directory access even when all known files are absent.
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                if (name.Equals(DatabaseFileName, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(DatabaseFileName + "-", StringComparison.OrdinalIgnoreCase)) database = true;
                else if (name.Equals("catalog-v1.json", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("catalog-v1.json.tmp", StringComparison.OrdinalIgnoreCase)) json = true;
                else if (name.Equals("operations", StringComparison.OrdinalIgnoreCase))
                {
                    RejectReparse(entry);
                    operations = Directory.EnumerateFileSystemEntries(entry).Any();
                }
                else if (name.Equals("backups", StringComparison.OrdinalIgnoreCase)) backups = entry;
                else if (!IsRetainedAuxiliaryEntry(name))
                    throw new IOException($"Unrecognized catalog evidence was retained at {entry}. Review it before choosing new storage.");
            }
            if (!database && !json && !operations && backups is not null)
            {
                RejectReparse(backups);
                if (Directory.EnumerateFileSystemEntries(backups).Any())
                    throw new IOException("Only catalog backups remain; recover or explicitly inspect the missing working catalog before creating new storage.");
            }
            return new(directory, database, json, operations, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(directory, database, json, operations, error.Message);
        }
    }

    internal static bool IsRetainedAuxiliaryEntry(string name) => name is
        "writer.lock" or "startup.lock" or "startup-v1.lock" or "storage-generations" or "storage-selection-v1.json" or
        "backups" or "diagnostics" or "logs" or "thumb-cache-v1" or "thumb-cache-v2" or "duplicate-searches" or
        "updates" or "program" ||
        name.StartsWith("storage-selection-v1.json.pending-", StringComparison.Ordinal);

    internal static CatalogStorageSelection ReadSelection(string localDirectory)
    {
        RejectReparse(localDirectory);
        var selected = ReadManifest(Path.Combine(localDirectory, SelectionFileName));
        if (selected.Version != 1 || !Guid.TryParseExact(selected.StorageId, "N", out _))
            throw new InvalidDataException("Unsupported or invalid catalog storage selector; no alternative catalog was opened.");
        var expectedDirectory = Path.Combine(localDirectory, GenerationDirectoryName, selected.StorageId);
        if (!Path.IsPathFullyQualified(selected.DirectoryPath) || !PathsEqual(selected.DirectoryPath, expectedDirectory) ||
            selected.SourceDirectory is not null && !Path.IsPathFullyQualified(selected.SourceDirectory))
            throw new InvalidDataException("Invalid catalog storage location; no alternative catalog was opened.");
        RejectReparse(Path.Combine(localDirectory, GenerationDirectoryName));
        RejectReparse(selected.DirectoryPath);
        if (ReadManifest(Path.Combine(selected.DirectoryPath, GenerationFileName)) != selected)
            throw new InvalidDataException("Catalog storage identity does not match its selector; no alternative catalog was opened.");
        var database = Path.Combine(selected.DirectoryPath, DatabaseFileName);
        RejectReparse(database);
        if ((File.GetAttributes(database) & FileAttributes.Directory) != 0)
            throw new InvalidDataException("The selected catalog database is not a file.");
        return selected;
    }

    private static CatalogStorageSelection ReadManifest(string path)
    {
        RejectReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 64 * 1024) throw new InvalidDataException("Catalog storage selector exceeds its supported size.");
        try { return JsonSerializer.Deserialize<CatalogStorageSelection>(stream) ?? throw new InvalidDataException("Empty catalog storage selector."); }
        catch (JsonException error) { throw new InvalidDataException("Catalog storage selector is unreadable; no alternative catalog was opened.", error); }
    }

    internal static void PublishManifest(string path, CatalogStorageSelection selection)
    {
        var pending = path + ".pending-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, selection);
            stream.Flush(true);
        }
        CatalogBackupPublication.Complete(pending, path);
    }
}
