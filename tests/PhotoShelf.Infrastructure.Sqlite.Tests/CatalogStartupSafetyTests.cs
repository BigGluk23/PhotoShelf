using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class CatalogStartupSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-startup-safe-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "catalog-v2.sqlite");
    private string Legacy => Path.Combine(_root, "catalog-v1.json");
    private SqliteDesktopCatalogStore Store => new(_root);

    [Theory]
    [InlineData("missing")]
    [InlineData("zero-byte")]
    [InlineData("empty-sqlite")]
    [InlineData("missing-import-marker")]
    [InlineData("missing-media-table")]
    [InlineData("older-schema")]
    public async Task PublishedGenerationCannotBeReinitializedOrImportedWhenItsEvidenceIsMissing(string damage)
    {
        await WriteLegacyAsync(LegacyState());
        switch (damage)
        {
            case "zero-byte": await File.WriteAllBytesAsync(Database, []); break;
            case "empty-sqlite": await ExecuteAsync("PRAGMA user_version=123;"); break;
            case "missing-import-marker":
            case "missing-media-table":
            case "older-schema":
                await Store.InitializeAndImportLegacyAsync(Legacy);
                await ExecuteAsync(damage switch
                {
                    "missing-import-marker" => "DELETE FROM desktop_settings WHERE key='legacy_json_import'; DROP INDEX ix_desktop_metadata_queue;",
                    "missing-media-table" => "DROP TABLE desktop_media_items;",
                    _ => "UPDATE desktop_settings SET value='4' WHERE key='catalog_schema'; DROP INDEX ix_desktop_metadata_queue;"
                });
                break;
        }
        SqliteConnection.ClearAllPools();
        var databaseBefore = File.Exists(Database) ? await File.ReadAllBytesAsync(Database) : null;
        var jsonBefore = await File.ReadAllBytesAsync(Legacy);
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.InitializeAndImportLegacyAsync(Legacy,
            requirePublishedGeneration: true));
        SqliteConnection.ClearAllPools();
        if (databaseBefore is null) Assert.False(File.Exists(Database));
        else Assert.Equal(databaseBefore, await File.ReadAllBytesAsync(Database));
        Assert.Equal(jsonBefore, await File.ReadAllBytesAsync(Legacy));
        Assert.False(Directory.Exists(Path.Combine(_root, "backups")));
    }

    [Fact]
    public async Task FreshEmptyGenerationCanBePreparedAndThenReopenedAsPublishedWithoutImportingLaterJson()
    {
        await Store.InitializeAndImportLegacyAsync(Legacy);
        await WriteLegacyAsync(LegacyState());
        await Store.InitializeAndImportLegacyAsync(Legacy, requirePublishedGeneration: true);
        var saved = await Store.LoadAsync();
        Assert.True(saved.ReadItemsFromSqlite);
        Assert.Empty(saved.Items);
        Assert.Equal("absent", await ScalarAsync("SELECT value FROM desktop_settings WHERE key='legacy_json_import';"));
    }

    [Theory]
    [InlineData("6")]
    [InlineData("2")]
    [InlineData("future")]
    [InlineData("")]
    public async Task UnsupportedSchemaIsRejectedBeforeAnyDatabaseMutation(string version)
    {
        await Store.InitializeAsync();
        await Store.UpsertItemsAsync([Item("keep.jpg", true)]);
        await ExecuteAsync("UPDATE desktop_settings SET value=$value WHERE key='catalog_schema'; DROP INDEX ix_desktop_metadata_queue;", version);
        SqliteConnection.ClearAllPools();
        var before = await File.ReadAllBytesAsync(Database);
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.ValidateCompatibilityAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.InitializeAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.InitializeAndImportLegacyAsync(Legacy));
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, await File.ReadAllBytesAsync(Database));
        Assert.Equal(version, await ScalarAsync("SELECT value FROM desktop_settings WHERE key='catalog_schema';"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name='ix_desktop_metadata_queue';"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM desktop_media_items WHERE is_favorite=1;"));
        Assert.False(Directory.Exists(Path.Combine(_root, "backups")));
    }

    [Fact]
    public async Task UnknownUnversionedLayoutIsRejectedWithoutAddingCatalogTables()
    {
        Directory.CreateDirectory(_root);
        await ExecuteAsync("CREATE TABLE unknown_format(value TEXT); INSERT INTO unknown_format VALUES('keep');");
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.InitializeAsync());
        Assert.Equal("keep", await ScalarAsync("SELECT value FROM unknown_format;"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name='desktop_settings';"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalePathOrFolderKeyLeftByOldWriterIsRejectedWithoutRepairOrIndexWrites(bool folderOnly)
    {
        await Store.InitializeAsync(); await Store.UpsertItemsAsync([Item(@"D:\Photos\before.jpg", true)]);
        await ExecuteAsync(folderOnly
            ? "UPDATE desktop_media_items SET folder_key='D:/WRONG'; DROP INDEX ix_desktop_metadata_queue;"
            : "UPDATE desktop_media_items SET path='D:\\Photos\\after.jpg'; DROP INDEX ix_desktop_metadata_queue;");
        SqliteConnection.ClearAllPools(); var before = await File.ReadAllBytesAsync(Database);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Store.ValidateCompatibilityAsync());
        Assert.Contains("соответствие путей", error.Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.InitializeAndImportLegacyAsync(Legacy));
        SqliteConnection.ClearAllPools(); Assert.Equal(before, await File.ReadAllBytesAsync(Database));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name='ix_desktop_metadata_queue';"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM desktop_media_items WHERE is_favorite=1;"));
        Assert.Equal("ok", await ScalarAsync("PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task KeyValidationUsesExistingInvariantWindowsNormalizationForUncExtendedAndUnicodePaths()
    {
        await Store.InitializeAsync();
        var paths = new[] { @"d:\Фото\Ёлка.JPG", @"\\server\Общий\photo.jpg", @"\\?\D:\Photos\a.jpg", @"\\?\UNC\server\share\a.jpg", "mixed/Slashes\\photo.jpg", "single.jpg" };
        await Store.UpsertItemsAsync(paths.Select(path => Item(path, true)));
        await Store.ValidateCompatibilityAsync(); await Store.InitializeAsync();
        Assert.Equal(paths.Order(), (await Store.LoadAsync()).Items.Select(item => item.Path).Order());
    }

    [Fact]
    public async Task MissingJsonBecomesExplicitlyAuthoritativeButCorruptAndUnreadableJsonDoNot()
    {
        Assert.Null(await LocalCatalogStore.ReadLegacyAsync(Legacy));
        await Store.InitializeAndImportLegacyAsync(Legacy);
        Assert.True((await Store.LoadAsync(includeItems: false)).ReadItemsFromSqlite);
        Assert.Equal("absent", await ScalarAsync("SELECT value FROM desktop_settings WHERE key='legacy_json_import';"));
        Assert.False(File.Exists(Legacy));

        await ExecuteAsync("DELETE FROM desktop_settings WHERE key='legacy_json_import';");
        await File.WriteAllTextAsync(Legacy, "{broken json");
        var bytes = await File.ReadAllBytesAsync(Legacy);
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.InitializeAndImportLegacyAsync(Legacy));
        Assert.False((await Store.LoadAsync(includeItems: false)).ReadItemsFromSqlite);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Legacy));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM desktop_settings;"));
        var inaccessibleAsFile = Path.Combine(_root, "directory-is-not-json"); Directory.CreateDirectory(inaccessibleAsFile);
        var failure = await Record.ExceptionAsync(() => Store.InitializeAndImportLegacyAsync(inaccessibleAsFile));
        Assert.True(failure is UnauthorizedAccessException or IOException);
        Assert.False((await Store.LoadAsync(includeItems: false)).ReadItemsFromSqlite);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":2,\"Items\":[]}")]
    [InlineData("{\"Version\":1,\"Items\":null}")]
    [InlineData("{\"Version\":1,\"Items\":[{\"Path\":\"a.jpg\"},{\"Path\":\"A.jpg\"}]}")]
    public async Task InvalidLegacyContentsCannotBeCertifiedAsAnEmptyCatalog(string json)
    {
        Directory.CreateDirectory(_root); await File.WriteAllTextAsync(Legacy, json);
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.InitializeAndImportLegacyAsync(Legacy));
        Assert.Equal(json, await File.ReadAllTextAsync(Legacy));
        Assert.Null(await ScalarAsync("SELECT value FROM desktop_settings WHERE key='legacy_json_import';"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM desktop_media_items;"));
    }

    [Fact]
    public async Task ImportCommitsFavoritesPreferencesHashesAndCompletionWithoutReadingOrRewritingOriginals()
    {
        var state = LegacyState(); await WriteLegacyAsync(state);
        var originalJson = await File.ReadAllBytesAsync(Legacy); var jsonTime = File.GetLastWriteTimeUtc(Legacy);
        await Store.InitializeAndImportLegacyAsync(Legacy);
        var saved = await Store.LoadAsync();
        Assert.True(saved.ReadItemsFromSqlite); Assert.Equal(2, saved.Items.Count);
        Assert.True(saved.Items.Single(item => item.Path == "never-opened-a.jpg").IsFavorite);
        Assert.Equal(state.TileWidth, saved.TileWidth); Assert.Equal(state.ViewMode, saved.ViewMode);
        Assert.Equal(state.ExpandedFolders, saved.ExpandedFolders); Assert.Equal(state.ExcludedFolders, saved.ExcludedFolders);
        Assert.Equal(state.IncludedFolders, saved.IncludedFolders); Assert.Equal(state.WatchedFolders, saved.WatchedFolders);
        Assert.Equal(state.QuarantineDirectory, saved.QuarantineDirectory);
        Assert.Equal(state.QuarantineBatchDirectories, saved.QuarantineBatchDirectories);
        Assert.True(saved.BackgroundProcessingPaused);
        Assert.Equal(state.DuplicateHashes[0].Hash, await ScalarAsync("SELECT sha256 FROM duplicate_hash_cache;"));
        var identities = saved.Items.Select(item => item.AssetId).ToArray();
        await Store.InitializeAndImportLegacyAsync(Legacy);
        Assert.Equal(identities, (await Store.LoadAsync()).Items.Select(item => item.AssetId));
        Assert.Equal(originalJson, await File.ReadAllBytesAsync(Legacy)); Assert.Equal(jsonTime, File.GetLastWriteTimeUtc(Legacy));
    }

    [Fact]
    public async Task AStraySettingDoesNotSkipImportAndPartialRowsResumeWithoutOverwritingTheirFavorite()
    {
        await Store.InitializeAsync(); await Store.UpsertItemsAsync([Item("never-opened-a.jpg", false)]);
        await ExecuteAsync("INSERT INTO desktop_settings(key,value) VALUES('future_setting','keep'),('view_mode','Recent');");
        Assert.False((await Store.LoadAsync(includeItems: false)).ReadItemsFromSqlite);
        await WriteLegacyAsync(LegacyState());
        await Store.InitializeAndImportLegacyAsync(Legacy);
        var state = await Store.LoadAsync(); Assert.Equal(2, state.Items.Count);
        Assert.False(state.Items.Single(item => item.Path == "never-opened-a.jpg").IsFavorite);
        Assert.Equal("Recent", state.ViewMode); Assert.Equal("keep", await ScalarAsync("SELECT value FROM desktop_settings WHERE key='future_setting';"));
        Assert.True(state.ReadItemsFromSqlite);
    }

    [Fact]
    public async Task VerifiedLegacyMetadataCacheSurvivesJsonOnlyImportWithoutReadingOfflineMedia()
    {
        var state = LegacyState(); await WriteLegacyAsync(state);
        var metadata = new MetadataIndexStore(_root); await metadata.InitializeAsync();
        var capture = new DateTime(2007, 2, 3);
        await metadata.SaveAsync(state.Items[0].Path, state.Items[0].SizeBytes, state.Items[0].FileModifiedAt, capture);
        // A stale cache entry for another file must not be attached to a different fingerprint.
        await metadata.SaveAsync(state.Items[1].Path, state.Items[1].SizeBytes + 1, state.Items[1].FileModifiedAt, capture);
        await Store.InitializeAndImportLegacyAsync(Legacy);
        var items = (await Store.LoadAsync()).Items;
        var known = items.Single(item => item.Path == state.Items[0].Path);
        Assert.Equal(capture, known.CaptureDate); Assert.True(known.MetadataIndexed);
        Assert.Null(items.Single(item => item.Path == state.Items[1].Path).CaptureDate);
    }

    [Fact]
    public async Task UnknownCompletionMarkerCannotHideAJsonImportOrChangeIndexes()
    {
        await Store.InitializeAsync(); await WriteLegacyAsync(LegacyState());
        await ExecuteAsync("INSERT INTO desktop_settings(key,value) VALUES('legacy_json_import','future-v2'); DROP INDEX ix_desktop_metadata_queue;");
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.InitializeAndImportLegacyAsync(Legacy));
        Assert.Equal("future-v2", await ScalarAsync("SELECT value FROM desktop_settings WHERE key='legacy_json_import';"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name='ix_desktop_metadata_queue';"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM desktop_media_items;"));
    }

    [Fact]
    public async Task KnownExistingEmptyCatalogDoesNotResurrectOldJsonAndDoesNotNeedToReadIt()
    {
        await Store.InitializeAsync(); await Store.SaveAsync(new LocalCatalogState { ViewMode = "Favorites" });
        // Model an established pre-v0.10.13 catalog: complete persisted settings, no new import marker.
        await ExecuteAsync("DELETE FROM desktop_settings WHERE key='legacy_json_import';");
        await File.WriteAllTextAsync(Legacy, "{invalid stale json");
        await Store.InitializeAndImportLegacyAsync(Legacy);
        Assert.Equal("Favorites", (await Store.LoadAsync()).ViewMode);
        Assert.Empty((await Store.LoadAsync()).Items);
        Assert.Equal("existing-sqlite", await ScalarAsync("SELECT value FROM desktop_settings WHERE key='legacy_json_import';"));
        Assert.Equal("{invalid stale json", await File.ReadAllTextAsync(Legacy));
    }

    [Fact]
    public async Task QuarantineAndMoveEvidencePreventsLegacyResurrectionEvenWithoutPreferences()
    {
        await Store.InitializeAsync(); await Store.UpsertItemsAsync([Item("never-opened-a.jpg", true)]);
        await Store.MoveItemAsync("never-opened-a.jpg", "quarantined.jpg", removeFromLibrary: true);
        await WriteLegacyAsync(LegacyState());
        await Store.InitializeAndImportLegacyAsync(Legacy);
        Assert.Empty((await Store.LoadAsync()).Items);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM desktop_media_items WHERE is_quarantined=1;"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM desktop_move_receipts;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSqlFailureRollsBackEarlierImportedRowsPreferencesAndMarkerThenRetrySucceeds(bool failAtCompletion)
    {
        await Store.InitializeAsync(); await WriteLegacyAsync(LegacyState());
        await ExecuteAsync(failAtCompletion ? """
            CREATE TRIGGER synthetic_import_failure BEFORE INSERT ON desktop_settings
            WHEN NEW.key='legacy_json_import' BEGIN SELECT RAISE(ABORT,'synthetic import failure'); END;
            """ : """
            CREATE TRIGGER synthetic_import_failure BEFORE INSERT ON desktop_media_items
            WHEN NEW.path='never-opened-b.jpg' BEGIN SELECT RAISE(ABORT,'synthetic import failure'); END;
            """);
        var json = await File.ReadAllBytesAsync(Legacy);
        await Assert.ThrowsAsync<SqliteException>(() => Store.InitializeAndImportLegacyAsync(Legacy));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM desktop_media_items;"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM desktop_size_counts;"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM desktop_settings;"));
        Assert.False((await Store.LoadAsync(includeItems: false)).ReadItemsFromSqlite);
        Assert.Equal(json, await File.ReadAllBytesAsync(Legacy));
        await ExecuteAsync("DROP TRIGGER synthetic_import_failure;");
        await Store.InitializeAndImportLegacyAsync(Legacy);
        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM desktop_media_items;"));
        Assert.True((await Store.LoadAsync(includeItems: false)).ReadItemsFromSqlite);
        Assert.Equal("ok", await ScalarAsync("PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task CancellationDoesNotMarkImportCompleteAndRetryIsSafe()
    {
        await Store.InitializeAsync(); await WriteLegacyAsync(LegacyState());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.InitializeAndImportLegacyAsync(Legacy, cancellation.Token));
        Assert.False((await Store.LoadAsync(includeItems: false)).ReadItemsFromSqlite);
        await Store.InitializeAndImportLegacyAsync(Legacy);
        Assert.Equal(2, (await Store.LoadAsync()).Items.Count);
    }

    private static SavedMediaItem Item(string path, bool favorite) => new()
    {
        Path = path, IsFavorite = favorite, SizeBytes = 123,
        FileModifiedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Local)
    };

    private static LocalCatalogState LegacyState() => new()
    {
        TileWidth = 192, ViewMode = "Favorites", ActiveFolder = @"D:\Photos", IncludeSystemFolders = true,
        SortNewestFirst = false, IncludeSubfolders = false, BackgroundProcessingPaused = true,
        ExpandedFolders = [@"D:\Photos"], ExcludedFolders = [@"D:\Private"], IncludedFolders = [@"D:\Private\Allowed"],
        WatchedFolders = [@"D:\Photos"], QuarantineDirectory = @"Q:\PhotoShelf", QuarantineBatchDirectories = [@"Q:\PhotoShelf\batch"],
        Items = [Item("never-opened-a.jpg", true), Item("never-opened-b.jpg", false)],
        DuplicateHashes = [new() { Path = "never-opened-a.jpg", SizeBytes = 123,
            FileModifiedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Local), Hash = new string('A', 64) }]
    };

    private async Task WriteLegacyAsync(LocalCatalogState state)
    { Directory.CreateDirectory(_root); await File.WriteAllTextAsync(Legacy, JsonSerializer.Serialize(state)); }
    private async Task ExecuteAsync(string sql, string? value = null)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False"); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        if (value is not null) command.Parameters.AddWithValue("$value", value); await command.ExecuteNonQueryAsync();
    }
    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False"); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync();
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
