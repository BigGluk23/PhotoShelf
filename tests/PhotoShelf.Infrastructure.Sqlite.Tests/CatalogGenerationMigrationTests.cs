using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Infrastructure.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class CatalogGenerationMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("photoshelf-storage-migration-test-").FullName;
    private string Local => Path.Combine(_root, "local");
    private string Roaming => Path.Combine(_root, "roaming");
    private string Pointer => Path.Combine(Local, "storage-selection-v1.json");
    private CatalogLocation Location() => new(Local, Roaming);

    [Fact]
    public async Task EmptyRoamingCannotStealExistingLocalCatalogAndFreshStorageCannotBypassIt()
    {
        await SeedAsync(Local);
        Directory.CreateDirectory(Roaming);
        var location = Location();
        var discovery = location.Discover();
        Assert.Null(discovery.Selection);
        var source = Assert.Single(discovery.Candidates, candidate => candidate.HasEvidence);
        Assert.Equal(Local, source.DirectoryPath);
        Assert.All(discovery.Candidates, candidate => Assert.True(candidate.IsAvailable));
        Assert.Throws<InvalidOperationException>(() => location.DirectoryPath);
        await Assert.ThrowsAsync<IOException>(() => new CatalogGenerationMigration().PrepareAsync(location, null));
        using var prepared = await new CatalogGenerationMigration().PrepareAsync(location, source);
        Assert.NotEqual(Local, prepared.DirectoryPath);
        Assert.False(File.Exists(Pointer));
        await AssertCatalogAsync(prepared.DirectoryPath);
        location.CommitPrepared(prepared);
        Assert.Equal(prepared.DirectoryPath, location.DirectoryPath);
    }

    [Fact]
    public async Task TwoRealCatalogsAreDiscoveredWithoutAutomaticallyChoosingOrMerging()
    {
        await SeedAsync(Local);
        await SeedAsync(Roaming);
        var location = Location();
        Assert.Equal(2, location.Discover().Candidates.Count(candidate => candidate.HasEvidence));
        Assert.Null(location.Selection);
        Assert.Throws<InvalidOperationException>(() => location.DirectoryPath);
        Assert.False(File.Exists(Pointer));
        var selected = location.Discover().Candidates.Single(candidate => candidate.DirectoryPath == Roaming);
        using var prepared = await new CatalogGenerationMigration().PrepareAsync(location, selected);
        location.CommitPrepared(prepared);
        Assert.Equal(Roaming, location.Selection!.SourceDirectory);
        prepared.Dispose();
        await AssertCatalogAsync(Local);
        await AssertCatalogAsync(Roaming);
    }

    [Fact]
    public async Task SnapshotRetainsSourceAndExactJsonJournalsAndUndoLinks()
    {
        await SeedAsync(Roaming);
        var operations = Directory.CreateDirectory(Path.Combine(Roaming, "operations")).FullName;
        var journal = Path.Combine(operations, "move.jsonl");
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"UndoJournalPath\":\"" + Path.Combine(operations, "undo.jsonl") + "\"}\npartial");
        await File.WriteAllBytesAsync(journal, bytes);
        await File.WriteAllTextAsync(Path.Combine(Roaming, "catalog-v1.json"), "{\"Version\":1,\"Items\":[]}");
        await File.WriteAllBytesAsync(journal + ".torn-123", [1, 2, 3, 4]);
        Directory.CreateDirectory(Path.Combine(Roaming, "backups"));
        await File.WriteAllTextAsync(Path.Combine(Roaming, "backups", "retained.sqlite"), "retained original backup");
        var databaseHash = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(Roaming, "catalog-v2.sqlite")));
        var location = Location();
        using (var prepared = await new CatalogGenerationMigration().PrepareAsync(location,
            location.Discover().Candidates.Single(candidate => candidate.HasEvidence)))
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(prepared.DirectoryPath, "operations", "move.jsonl")));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(prepared.DirectoryPath, "operations", "move.jsonl.torn-123")));
            Assert.Equal(await File.ReadAllTextAsync(Path.Combine(Roaming, "catalog-v1.json")),
                await File.ReadAllTextAsync(Path.Combine(prepared.DirectoryPath, "catalog-v1.json")));
            Assert.False(Directory.Exists(Path.Combine(prepared.DirectoryPath, "backups")));
            location.CommitPrepared(prepared);
        }
        Assert.Equal(databaseHash, SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(Roaming, "catalog-v2.sqlite"))));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(journal));
        var reopened = Location();
        var selected = reopened.Discover().Selection!;
        reopened.PinExisting(selected);
        Assert.Equal(location.DirectoryPath, reopened.DirectoryPath);
        Assert.Equal(Roaming, selected.SourceDirectory);
        Assert.False(reopened.UsesLegacyStorage);
        await AssertCatalogAsync(reopened.DirectoryPath);
    }

    [Fact]
    public async Task BackupIncludesCommittedWalPagesRatherThanOnlyTheMainFile()
    {
        await SeedAsync(Roaming);
        await using var writer = Open(Roaming);
        await writer.OpenAsync();
        await using var change = writer.CreateCommand();
        change.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO desktop_settings VALUES('wal-first','first');";
        await change.ExecuteNonQueryAsync();
        await using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(Roaming, "catalog-v2.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await reader.OpenAsync();
        await using var read = reader.CreateCommand();
        // This old read snapshot keeps the later committed value in WAL after the writer closes.
        read.CommandText = "BEGIN; SELECT value FROM desktop_settings WHERE key='wal-first';";
        Assert.Equal("first", await read.ExecuteScalarAsync());
        change.CommandText = "INSERT INTO desktop_settings VALUES('wal-only','must survive');";
        await change.ExecuteNonQueryAsync();
        await writer.CloseAsync();
        Assert.True(new FileInfo(Path.Combine(Roaming, "catalog-v2.sqlite-wal")).Length > 32);

        var mainOnly = Path.Combine(_root, "deliberately-incomplete-main-only.sqlite");
        File.Copy(Path.Combine(Roaming, "catalog-v2.sqlite"), mainOnly); // Synthetic negative control, never production backup logic.
        await using (var incomplete = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = mainOnly, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await incomplete.OpenAsync();
            await using var missing = incomplete.CreateCommand();
            missing.CommandText = "SELECT value FROM desktop_settings WHERE key='wal-only';";
            Assert.Null(await missing.ExecuteScalarAsync());
        }
        var location = Location();
        using var prepared = await new CatalogGenerationMigration().PrepareAsync(location,
            location.Discover().Candidates.Single(candidate => candidate.HasEvidence));
        await using var copied = Open(prepared.DirectoryPath);
        await copied.OpenAsync();
        await using var verify = copied.CreateCommand();
        verify.CommandText = "SELECT value FROM desktop_settings WHERE key='wal-only';";
        Assert.Equal("must survive", await verify.ExecuteScalarAsync());
        verify.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await verify.ExecuteScalarAsync());
        location.CommitPrepared(prepared);
    }

    [Fact]
    public async Task LaterLegacyDatabaseWritesCannotChangePinnedWorkingGeneration()
    {
        await SeedAsync(Roaming);
        var location = Location();
        using (var prepared = await new CatalogGenerationMigration().PrepareAsync(location,
            location.Discover().Candidates.Single(candidate => candidate.HasEvidence)))
            location.CommitPrepared(prepared);
        // Deliberately simulate an old application's destructive catalog save, only on this synthetic source.
        await using var source = Open(Roaming);
        await source.OpenAsync();
        await using var change = source.CreateCommand();
        change.CommandText = "DELETE FROM desktop_media_items; UPDATE desktop_settings SET value='All' WHERE key='view_mode';";
        await change.ExecuteNonQueryAsync();
        await AssertCatalogAsync(location.DirectoryPath);
        Assert.Equal(location.Selection, Location().Discover().Selection);
    }

    [Theory]
    [InlineData("source-copied")]
    [InlineData("before-generation-manifest")]
    [InlineData("generation-manifest-published")]
    public async Task InterruptionBeforeSelectorPreservesSourceAndDoesNotSelectIncompleteGeneration(string cut)
    {
        await SeedAsync(Roaming);
        var location = Location();
        var migration = new CatalogGenerationMigration(new() { Checkpoint = stage => { if (stage == cut) throw new SimulatedCrash(); } });
        PreparedCatalogGeneration? prepared = null;
        try
        {
            var failure = await Record.ExceptionAsync(async () =>
            {
                prepared = await migration.PrepareAsync(location, location.Discover().Candidates.Single(candidate => candidate.HasEvidence));
                location.CommitPrepared(prepared);
            });
            Assert.IsType<SimulatedCrash>(failure);
            Assert.False(File.Exists(Pointer));
            if (cut == "generation-manifest-published")
                Assert.Throws<InvalidDataException>(() => Location().Discover());
            else
                Assert.Null(Location().Discover().Selection);
            Assert.NotEmpty(Directory.GetDirectories(Path.Combine(Local, "storage-generations")));
        }
        finally { prepared?.Dispose(); }
        await AssertCatalogAsync(Roaming);
        var retry = Location();
        if (cut == "generation-manifest-published")
        {
            // A missing selector could also be a lost pointer to newer user work.
            // Preserve this complete generation and require explicit recovery instead of choosing an older source.
            await Assert.ThrowsAsync<InvalidDataException>(() => new CatalogGenerationMigration().PrepareAsync(retry, null));
            return;
        }
        using var retried = await new CatalogGenerationMigration().PrepareAsync(retry,
            retry.Discover().Candidates.Single(candidate => candidate.HasEvidence));
        retry.CommitPrepared(retried);
        await AssertCatalogAsync(retry.DirectoryPath);
    }

    [Fact]
    public async Task LostSelectorCannotReactivateRetainedSourceOrHideNewerGenerationUserData()
    {
        await SeedAsync(Roaming);
        var location = Location();
        var originalCandidate = location.Discover().Candidates.Single(candidate => candidate.HasEvidence);
        using (var prepared = await new CatalogGenerationMigration().PrepareAsync(location, originalCandidate))
            location.CommitPrepared(prepared);
        var active = new SqliteDesktopCatalogStore(location.DirectoryPath);
        await active.SaveAsync(new LocalCatalogState { ViewMode = "Folder", ActiveFolder = "D:\\new-user-library",
            Items = [new SavedMediaItem { Path = "D:\\new-user-library\\new-favorite.jpg", IsFavorite = true, SizeBytes = 777 }] });
        SqliteConnection.ClearAllPools();
        File.Delete(Pointer); // Synthetic loss of selection metadata after the new generation has been used.
        var restarted = Location();
        var error = Assert.Throws<InvalidDataException>(() => restarted.Discover());
        Assert.Contains(location.DirectoryPath, error.Message);
        Assert.Contains(Pointer, error.Message);
        Assert.Throws<InvalidOperationException>(() => restarted.DirectoryPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => new CatalogGenerationMigration().PrepareAsync(restarted, null));
        await Assert.ThrowsAsync<InvalidDataException>(() => new CatalogGenerationMigration().PrepareAsync(restarted, originalCandidate));
        Assert.False(File.Exists(Pointer));
        var retainedCurrent = await active.LoadAsync();
        Assert.Equal("D:\\new-user-library", retainedCurrent.ActiveFolder);
        Assert.Equal(2, retainedCurrent.Items.Count);
        Assert.Contains(retainedCurrent.Items, item => item.Path.EndsWith("new-favorite.jpg") && item.IsFavorite && item.SizeBytes == 777);
        await AssertCatalogAsync(Roaming);
    }

    [Fact]
    public async Task InterruptionAfterSelectorCanReopenOnlyFullyPreparedGeneration()
    {
        await SeedAsync(Roaming);
        var location = Location();
        using var prepared = await new CatalogGenerationMigration(new() { Checkpoint = stage =>
        { if (stage == "selector-published") throw new SimulatedCrash(); } }).PrepareAsync(location,
            location.Discover().Candidates.Single(candidate => candidate.HasEvidence));
        Assert.Throws<SimulatedCrash>(() => location.CommitPrepared(prepared));
        var reopened = Location();
        var selected = reopened.Discover().Selection!;
        reopened.PinExisting(selected);
        Assert.Equal(prepared.DirectoryPath, reopened.DirectoryPath);
        await AssertCatalogAsync(reopened.DirectoryPath);
    }

    [Fact]
    public async Task SourceWriterLeasePreventsCopyAndExistingSelectorIsNeverOverwritten()
    {
        await SeedAsync(Roaming);
        var location = Location();
        var candidate = location.Discover().Candidates.Single(item => item.HasEvidence);
        using (var lease = new FileStream(Path.Combine(Roaming, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => new CatalogGenerationMigration().PrepareAsync(location, candidate));
        Assert.False(File.Exists(Pointer));
        using var prepared = await new CatalogGenerationMigration().PrepareAsync(location, candidate);
        location.CommitPrepared(prepared);
        var pointer = await File.ReadAllBytesAsync(Pointer);
        await Assert.ThrowsAsync<IOException>(() => new CatalogGenerationMigration().PrepareAsync(Location(), candidate));
        Assert.Equal(pointer, await File.ReadAllBytesAsync(Pointer));
    }

    [Fact]
    public async Task NewOperationEvidenceDuringPreparationPreventsPublishingSelector()
    {
        await SeedAsync(Roaming);
        var location = Location();
        using var prepared = await new CatalogGenerationMigration().PrepareAsync(location,
            location.Discover().Candidates.Single(candidate => candidate.HasEvidence));
        Directory.CreateDirectory(Path.Combine(Roaming, "operations"));
        await File.WriteAllTextAsync(Path.Combine(Roaming, "operations", "late.jsonl"), "new evidence");
        Assert.Throws<IOException>(() => location.CommitPrepared(prepared));
        Assert.False(File.Exists(Pointer));
        Assert.Equal("new evidence", await File.ReadAllTextAsync(Path.Combine(Roaming, "operations", "late.jsonl")));
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("unknown-version")]
    [InlineData("missing-database")]
    [InlineData("wrong-identity")]
    public async Task BrokenPinnedStorageFailsClosedInsteadOfFallingBackToLegacy(string damage)
    {
        await SeedAsync(Roaming);
        var location = Location();
        using (var prepared = await new CatalogGenerationMigration().PrepareAsync(location,
            location.Discover().Candidates.Single(candidate => candidate.HasEvidence)))
            location.CommitPrepared(prepared);
        SqliteConnection.ClearAllPools();
        if (damage == "unreadable") await File.WriteAllTextAsync(Pointer, "{not a manifest");
        if (damage == "unknown-version") await File.WriteAllTextAsync(Pointer, JsonSerializer.Serialize(location.Selection! with { Version = 99 }));
        if (damage == "missing-database") File.Delete(Path.Combine(location.DirectoryPath, "catalog-v2.sqlite"));
        if (damage == "wrong-identity") await File.WriteAllTextAsync(Path.Combine(location.DirectoryPath, "storage-generation-v1.json"),
            JsonSerializer.Serialize(location.Selection! with { SourceDirectory = Local }));
        var reopened = Location();
        Assert.NotNull(Record.Exception(() => reopened.Discover()));
        Assert.Throws<InvalidOperationException>(() => reopened.DirectoryPath);
        await AssertCatalogAsync(Roaming);
    }

    [Fact]
    public async Task UnknownEvidenceAndOrphanedWalAreNeverTreatedAsEmptyCatalog()
    {
        Directory.CreateDirectory(Roaming);
        await File.WriteAllTextAsync(Path.Combine(Roaming, "catalog-v99.sqlite"), "unknown format");
        var location = Location();
        Assert.Contains(location.Discover().Candidates, candidate => !candidate.IsAvailable);
        await Assert.ThrowsAsync<IOException>(() => new CatalogGenerationMigration().PrepareAsync(location, null));
        File.Move(Path.Combine(Roaming, "catalog-v99.sqlite"), Path.Combine(Roaming, "catalog-v2.sqlite-wal"));
        var source = Assert.Single(location.Discover().Candidates, candidate => candidate.HasEvidence);
        await Assert.ThrowsAsync<IOException>(() => new CatalogGenerationMigration().PrepareAsync(location, source));
        Assert.False(File.Exists(Pointer));
        Assert.Equal("unknown format", await File.ReadAllTextAsync(Path.Combine(Roaming, "catalog-v2.sqlite-wal")));
    }

    [Fact]
    public async Task RetainedBackupWithoutWorkingCatalogRequiresRecoveryRatherThanFreshEmptyStorage()
    {
        var backups = Directory.CreateDirectory(Path.Combine(Roaming, "backups")).FullName;
        var location = Location();
        Assert.All(location.Discover().Candidates, candidate => Assert.True(candidate.IsAvailable));
        await File.WriteAllTextAsync(Path.Combine(backups, "last-good.sqlite"), "backup evidence");
        Assert.Contains(location.Discover().Candidates, candidate => candidate.DirectoryPath == Roaming && !candidate.IsAvailable);
        await Assert.ThrowsAsync<IOException>(() => new CatalogGenerationMigration().PrepareAsync(location, null));
        Assert.False(File.Exists(Pointer));
        Assert.Equal("backup evidence", await File.ReadAllTextAsync(Path.Combine(backups, "last-good.sqlite")));
    }

    [Fact]
    public async Task CatalogDirectoryLinkIsReportedAsUnavailableRatherThanEmpty()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "linked-source")).FullName;
        Directory.CreateSymbolicLink(Roaming, target);
        var location = Location();
        Assert.Contains(location.Discover().Candidates, candidate => candidate.DirectoryPath == Roaming && !candidate.IsAvailable);
        await Assert.ThrowsAsync<IOException>(() => new CatalogGenerationMigration().PrepareAsync(location, null));
    }

    private static async Task SeedAsync(string directory)
    {
        var store = new SqliteDesktopCatalogStore(directory);
        await store.InitializeAsync();
        await store.SaveAsync(new LocalCatalogState { ViewMode = "Favorites", DateGroupingMode = "CaptureDate",
            Items = [new SavedMediaItem { Path = "D:\\synthetic\\photo.jpg", IsFavorite = true, SizeBytes = 123,
                CaptureDate = new DateTime(2001, 2, 3), MetadataIndexed = true }] });
        SqliteConnection.ClearAllPools();
    }

    private static async Task AssertCatalogAsync(string directory)
    {
        var store = new SqliteDesktopCatalogStore(directory);
        var state = await store.LoadAsync();
        var item = Assert.Single(state.Items);
        Assert.True(item.IsFavorite);
        Assert.Equal(123, item.SizeBytes);
        Assert.Equal(new DateTime(2001, 2, 3), item.CaptureDate);
        Assert.Equal("Favorites", state.ViewMode);
        Assert.Equal("CaptureDate", state.DateGroupingMode);
        SqliteConnection.ClearAllPools();
    }

    private static SqliteConnection Open(string directory) => new(new SqliteConnectionStringBuilder
    { DataSource = Path.Combine(directory, "catalog-v2.sqlite"), Pooling = false }.ToString());
    private sealed class SimulatedCrash : Exception { }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
