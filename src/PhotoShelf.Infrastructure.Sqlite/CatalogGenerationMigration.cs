using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace PhotoShelf.Infrastructure.Sqlite;

/// <summary>Fault-injection checkpoints operate on synthetic fixtures in tests; normal startup supplies no callback.</summary>
public sealed class CatalogGenerationMigrationOptions
{
    public Action<string>? Checkpoint { get; init; }
}

/// <summary>
/// Makes an isolated working copy. The caller holds the application startup barrier and validates
/// compatibility first; old applications must be closed before this operation begins.
/// </summary>
public sealed class CatalogGenerationMigration
{
    private readonly CatalogGenerationMigrationOptions _options;
    public CatalogGenerationMigration(CatalogGenerationMigrationOptions? options = null) => _options = options ?? new();

    public Task<PreparedCatalogGeneration> PrepareAsync(CatalogLocation location, CatalogStorageCandidate? source,
        CancellationToken token = default) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        if (location.Selection is not null || location.IsIsolatedSmoke)
            throw new InvalidOperationException("The catalog location is already selected.");
        if (CatalogStorageFiles.Exists(Path.Combine(location.LocalDirectory, CatalogStorageFiles.SelectionFileName)))
            throw new IOException("A storage selector already exists; it must be validated instead of replaced.");
        var discovery = location.Discover(); // Also refuses a missing selector beside a completed generation.
        if (source is null && discovery.Candidates.Any(candidate => candidate.HasEvidence || !candidate.IsAvailable))
            throw new IOException("Existing or inaccessible catalog evidence must be resolved before creating fresh storage.");
        if (source is not null && (!source.IsAvailable || !source.HasEvidence ||
            !CatalogStorageFiles.PathsEqual(source.DirectoryPath, location.LocalDirectory) &&
            !CatalogStorageFiles.PathsEqual(source.DirectoryPath, location.LegacyDirectory)))
            throw new InvalidOperationException("Only an available, explicitly discovered catalog can be migrated.");
        if (source is not null && CatalogStorageFiles.InspectCandidate(source.DirectoryPath) != source)
            throw new IOException("The catalog candidate changed after discovery; repeat storage selection.");

        Directory.CreateDirectory(location.LocalDirectory);
        CatalogStorageFiles.RejectReparse(location.LocalDirectory);
        var generations = Path.Combine(location.LocalDirectory, CatalogStorageFiles.GenerationDirectoryName);
        Directory.CreateDirectory(generations);
        CatalogStorageFiles.RejectReparse(generations);
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(generations, id);
        Directory.CreateDirectory(directory);
        var prepared = new PreparedCatalogGeneration(location, id, directory, source?.DirectoryPath, _options);
        try
        {
            if (source is not null)
            {
                prepared.AcquireSource();
                _options.Checkpoint?.Invoke("source-locked");
                foreach (var entry in prepared.SourceFiles)
                {
                    token.ThrowIfCancellationRequested();
                    var destination = Path.Combine(directory, entry.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (entry.RelativePath == CatalogStorageFiles.DatabaseFileName)
                        await CopyDatabaseAsync(entry.Stream.Name, destination, token);
                    else
                        await CopyExactAsync(entry.Stream, destination, token);
                }
                prepared.ValidateSource();
            }
            _options.Checkpoint?.Invoke("source-copied");
            token.ThrowIfCancellationRequested();
            return prepared;
        }
        catch
        {
            // Preserve every incomplete generation for diagnosis. It has no published selector.
            prepared.Dispose();
            throw;
        }
    }, token);

    private static async Task CopyDatabaseAsync(string sourcePath, string destinationPath, CancellationToken token)
    {
        var pending = destinationPath + ".pending";
        await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = pending, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            await source.OpenAsync(token);
            await using (var check = source.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check;";
                if (!Equals(await check.ExecuteScalarAsync(token), "ok"))
                    throw new InvalidDataException("Source catalog integrity check failed; it was not changed.");
            }
            await destination.OpenAsync(token);
            token.ThrowIfCancellationRequested();
            source.BackupDatabase(destination); // Includes committed WAL pages; never copy the main file alone.
            token.ThrowIfCancellationRequested();
            await using var verify = destination.CreateCommand();
            verify.CommandText = "PRAGMA integrity_check;";
            if (!Equals(await verify.ExecuteScalarAsync(token), "ok"))
                throw new InvalidDataException("The isolated catalog backup did not pass verification.");
        }
        using (var file = new FileStream(pending, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) file.Flush(true);
        token.ThrowIfCancellationRequested();
        CatalogBackupPublication.Complete(pending, destinationPath);
    }

    private static async Task CopyExactAsync(FileStream source, string destination, CancellationToken token)
    {
        var pending = destination + ".pending-" + Guid.NewGuid().ToString("N");
        source.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            var buffer = new byte[65536];
            int count;
            while ((count = await source.ReadAsync(buffer, token)) != 0)
            {
                hash.AppendData(buffer.AsSpan(0, count));
                await output.WriteAsync(buffer.AsMemory(0, count), token);
            }
            output.Flush(true);
        }
        await using (var copied = new FileStream(pending, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
        {
            var copiedHash = await SHA256.HashDataAsync(copied, token);
            if (!hash.GetHashAndReset().AsSpan().SequenceEqual(copiedHash))
                throw new IOException("A settings/journal copy failed byte verification; the source was not changed.");
        }
        token.ThrowIfCancellationRequested();
        CatalogBackupPublication.Complete(pending, destination);
    }
}

public sealed class PreparedCatalogGeneration : IDisposable
{
    private readonly CatalogGenerationMigrationOptions _options;
    private FileStream? _sourceLease;
    private bool _disposed;
    private bool _published;
    internal CatalogLocation Location { get; }
    public string StorageId { get; }
    public string DirectoryPath { get; }
    public string? SourceDirectory { get; }
    internal List<SourceCatalogFile> SourceFiles { get; } = [];

    internal PreparedCatalogGeneration(CatalogLocation location, string storageId, string directory,
        string? source, CatalogGenerationMigrationOptions options)
    { Location = location; StorageId = storageId; DirectoryPath = directory; SourceDirectory = source; _options = options; }

    internal void AcquireSource()
    {
        CatalogStorageFiles.RejectReparse(SourceDirectory!);
        var leasePath = Path.Combine(SourceDirectory!, "writer.lock");
        if (CatalogStorageFiles.Exists(leasePath)) CatalogStorageFiles.RejectReparse(leasePath);
        _sourceLease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (var relative in EnumerateSourceFiles())
        {
            var path = Path.Combine(SourceDirectory!, relative);
            CatalogStorageFiles.RejectReparse(path);
            // On Windows these handles prohibit old processes from modifying or replacing the snapshot inputs.
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            SourceFiles.Add(new(relative, stream, stream.Length, File.GetLastWriteTimeUtc(path)));
        }
    }

    internal void ValidateSource()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (SourceDirectory is null) return;
        if (!EnumerateSourceFiles().SequenceEqual(SourceFiles.Select(x => x.RelativePath), StringComparer.Ordinal))
            throw new IOException("Catalog settings or operation journals changed during the copy; the new storage was not selected.");
        foreach (var source in SourceFiles)
            if (source.Stream.Length != source.Length || File.GetLastWriteTimeUtc(source.Stream.Name) != source.ModifiedUtc)
                throw new IOException("A catalog snapshot input changed; the new storage was not selected.");
    }

    private string[] EnumerateSourceFiles()
    {
        var files = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(SourceDirectory!))
        {
            var name = Path.GetFileName(path);
            if (name is "catalog-v2.sqlite" or "catalog-v1.json" or "catalog-v1.json.tmp")
            {
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException($"Expected a catalog file: {path}");
                files.Add(name);
            }
            else if (name == "operations")
            {
                var pending = new Stack<string>(); pending.Push(path);
                while (pending.TryPop(out var folder))
                {
                    CatalogStorageFiles.RejectReparse(folder);
                    foreach (var child in Directory.EnumerateFileSystemEntries(folder))
                    {
                        CatalogStorageFiles.RejectReparse(child);
                        if ((File.GetAttributes(child) & FileAttributes.Directory) != 0) pending.Push(child);
                        else files.Add(Path.GetRelativePath(SourceDirectory!, child));
                        if (files.Count > 10000) throw new IOException("Too many catalog evidence files for one safe migration; all originals were retained.");
                    }
                }
            }
            else if (name is "catalog-v2.sqlite-wal" or "catalog-v2.sqlite-shm" or "catalog-v2.sqlite-journal" or
                "writer.lock" || CatalogStorageFiles.IsRetainedAuxiliaryEntry(name)) { }
            else throw new IOException($"Unrecognized catalog evidence was retained at {path}. Review it before choosing new storage.");
        }
        if (new[] { "catalog-v2.sqlite-wal", "catalog-v2.sqlite-shm", "catalog-v2.sqlite-journal" }
                .Any(name => CatalogStorageFiles.Exists(Path.Combine(SourceDirectory!, name))) &&
            !files.Contains(CatalogStorageFiles.DatabaseFileName))
            throw new IOException("Catalog sidecars have no main database; recover the source before migrating.");
        if (files.Contains("catalog-v1.json.tmp") && !files.Contains("catalog-v1.json"))
            throw new IOException("Only a pending legacy JSON catalog exists; recover it before migrating.");
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    internal CatalogStorageSelection Publish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_published) throw new InvalidOperationException("The generation was already published.");
        ValidateSource();
        var database = Path.Combine(DirectoryPath, CatalogStorageFiles.DatabaseFileName);
        CatalogStorageFiles.RejectReparse(database); // Caller must initialize/validate/import before committing.
        _options.Checkpoint?.Invoke("before-generation-manifest");
        var selection = new CatalogStorageSelection(1, StorageId, DirectoryPath, SourceDirectory);
        CatalogStorageFiles.PublishManifest(Path.Combine(DirectoryPath, CatalogStorageFiles.GenerationFileName), selection);
        _options.Checkpoint?.Invoke("generation-manifest-published");
        CatalogStorageFiles.PublishManifest(Path.Combine(Location.LocalDirectory, CatalogStorageFiles.SelectionFileName), selection);
        _published = true;
        _options.Checkpoint?.Invoke("selector-published");
        return selection;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var source in SourceFiles) source.Stream.Dispose();
        _sourceLease?.Dispose();
    }

    internal sealed record SourceCatalogFile(string RelativePath, FileStream Stream, long Length, DateTime ModifiedUtc);
}
