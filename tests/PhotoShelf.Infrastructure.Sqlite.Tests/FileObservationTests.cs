using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Metadata;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class FileObservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-observations-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Modified = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Capture = new(2020, 1, 2);
    private static readonly DateTime Checked = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
    private const string Original = @"D:\Photos\a.jpg";
    private static SavedMediaItem Observed(string path = Original, string? identity = "volume:file:creation") => new()
    {
        Path = path, SizeBytes = 42, FileModifiedAt = Modified, FileIdentity = identity,
        Availability = FileAvailability.Available, AvailabilityCheckedAtUtc = Checked
    };
    private async Task<SqliteDesktopCatalogStore> CreateAsync()
    {
        var store = new SqliteDesktopCatalogStore(_root); await store.InitializeAsync();
        var item = Observed(); item.CaptureDate = Capture; item.MetadataIndexed = true; item.IsFavorite = true;
        await store.UpsertItemsAsync([item]); return store;
    }

    [Theory]
    [InlineData(FileAvailability.Missing)]
    [InlineData(FileAvailability.RootOffline)]
    [InlineData(FileAvailability.AccessDenied)]
    [InlineData(FileAvailability.NeedsVerification)]
    public async Task UnavailableObservationPreservesKnownMetadataIdentityAndFavorites(FileAvailability availability)
    {
        var store = await CreateAsync(); var before = (await store.GetItemAsync(Original))!;
        Assert.True(await store.SetAvailabilityAsync(before, availability, Checked.AddMinutes(1), "probe-error"));
        var after = (await store.GetItemAsync(Original))!;
        Assert.Equal(before.AssetId, after.AssetId); Assert.True(after.IsFavorite); Assert.Equal(Capture, after.CaptureDate);
        Assert.Equal(42, after.SizeBytes); Assert.Equal(Modified, after.FileModifiedAt!.Value.ToUniversalTime());
        Assert.Equal(before.FileIdentity, after.FileIdentity); Assert.Equal(availability, after.Availability);
        Assert.Equal("probe-error", after.AvailabilityErrorCode); Assert.Equal(before.ObservationVersion + 1, after.ObservationVersion);
        Assert.Equal(1, await store.CountAsync(new()));
        Assert.False(await store.SetAvailabilityAsync(before, FileAvailability.Missing, Checked.AddMinutes(2)));
        await store.UpsertItemsAsync([new() { Path = Original, SizeBytes = 42, FileModifiedAt = Modified }]);
        Assert.Equal(availability, (await store.GetItemAsync(Original))!.Availability);
    }

    [Fact] public async Task SameFingerprintEventInvalidatesHashAndRejectsStaleMetadataAndHashCompletions()
    {
        var store = await CreateAsync(); var hashes = new DuplicateHashStore(_root); await hashes.InitializeAsync();
        var before = (await store.GetItemAsync(Original))!;
        var hash = new SavedDuplicateHash { Path = Original, SizeBytes = 42, FileModifiedAt = before.FileModifiedAt, Hash = "old" };
        Assert.True(await hashes.SaveObservedAsync(hash, before));
        Assert.NotNull(await hashes.TryGetAsync(Original, 42, before.FileModifiedAt));
        Assert.True(await store.ApplyObservationAsync(new(Observed(), ForceContentRevalidation: true), before));
        var after = (await store.GetItemAsync(Original))!;
        Assert.False(after.MetadataIndexed); Assert.Equal(Capture, after.CaptureDate); // suspicion is not authoritative absence
        Assert.Null(await hashes.TryGetAsync(Original, 42, before.FileModifiedAt));
        Assert.False(await hashes.SaveObservedAsync(hash, before));
        Assert.False(await store.UpdateMetadataResultAsync(Original, 42, Modified, new(MetadataReadStatus.Absent), Checked,
            expectedObservationVersion: before.ObservationVersion));
        Assert.True(await store.UpdateMetadataResultAsync(Original, 42, Modified, new(MetadataReadStatus.TransientError, ErrorCode: "locked"), Checked,
            expectedObservationVersion: after.ObservationVersion));
        Assert.Equal(Capture, (await store.GetItemAsync(Original))!.CaptureDate);
    }

    [Fact] public async Task CaseOnlyRenamePreservesAssetAndUpdatesDisplayPath()
    {
        var store = await CreateAsync(); var before = (await store.GetItemAsync(Original))!;
        var current = Observed(Original.ToUpperInvariant());
        Assert.True(await store.ApplyObservationAsync(new(current), before));
        var after = (await store.GetItemAsync(Original))!;
        Assert.Equal(current.Path, after.Path); Assert.Equal(before.AssetId, after.AssetId);
        Assert.Equal(Capture, after.CaptureDate); Assert.True(after.IsFavorite);
    }

    [Fact] public async Task ForcedCaseOnlyRenameInvalidatesOrphanCacheUnderNewSpelling()
    {
        var store = await CreateAsync(); var hashes = new DuplicateHashStore(_root); await hashes.InitializeAsync();
        var before = (await store.GetItemAsync(Original))!; var current = Observed(Original.ToUpperInvariant());
        await hashes.SaveAsync([new() { Path = current.Path, SizeBytes = 42, FileModifiedAt = before.FileModifiedAt, Hash = "orphan" }]);
        Assert.NotNull(await hashes.TryGetAsync(current.Path, 42, before.FileModifiedAt));
        Assert.True(await store.ApplyObservationAsync(new(current, true), before));
        Assert.Null(await hashes.TryGetAsync(current.Path, 42, before.FileModifiedAt));
    }

    [Fact] public async Task NewObservedFileCannotReuseOrphanHashOrCaptureCache()
    {
        var store = await CreateAsync(); var hashes = new DuplicateHashStore(_root); await hashes.InitializeAsync();
        var metadata = new MetadataIndexStore(_root); await metadata.InitializeAsync();
        var path = @"D:\Photos\new.jpg"; var observed = Observed(path);
        await hashes.SaveAsync([new() { Path = path, SizeBytes = 42, FileModifiedAt = Modified.ToLocalTime(), Hash = "orphan" }]);
        await metadata.SaveAsync(path, 42, Modified, Capture);
        Assert.True(await store.ApplyObservationAsync(new(observed)));
        Assert.Null(await hashes.TryGetAsync(path, 42, Modified.ToLocalTime()));
        Assert.Empty(await metadata.LoadCaptureDatesAsync());
        Assert.Null((await store.GetItemAsync(path))!.CaptureDate);
    }

    [Theory]
    [InlineData(FileAvailability.Missing, "missing", false)]
    [InlineData(FileAvailability.RootOffline, "offline", false)]
    [InlineData(FileAvailability.AccessDenied, "denied", false)]
    [InlineData(FileAvailability.NeedsVerification, "ReparsePoint", false)]
    [InlineData(FileAvailability.NeedsVerification, "PhysicalPathChanged", false)]
    [InlineData(FileAvailability.NeedsVerification, null, true)]
    public async Task KnownProbeFailureIsVisibleButExcludedFromMetadataAndDuplicateReads(FileAvailability availability, string? error, bool eligible)
    {
        var store = await CreateAsync(); var hashes = new DuplicateHashStore(_root); await hashes.InitializeAsync();
        await store.UpsertItemsAsync([Observed(@"D:\Photos\other.jpg", "other-identity")]);
        await store.ResetMetadataIndexAsync(); var before = (await store.GetItemAsync(Original))!;
        Assert.True(await store.SetAvailabilityAsync(before, availability, Checked.AddMinutes(1), error));
        var current = (await store.GetItemAsync(Original))!;
        Assert.Equal(1, await store.CountAsync(new() { SearchText = "/A.JPG" }));
        Assert.Equal(eligible ? 1 : 0, await store.CountAsync(new() { SearchText = "/A.JPG", MetadataDueAtUtc = Checked.AddMinutes(2) }));
        Assert.Equal(eligible ? 1 : 0, await store.CountAsync(new() { SearchText = "/A.JPG", DuplicateCandidatesOnly = true }));
        Assert.Equal(eligible, await store.UpdateMetadataResultAsync(Original, 42, Modified, new(MetadataReadStatus.Found, Capture), Checked.AddMinutes(2),
            expectedObservationVersion: current.ObservationVersion));
        Assert.Equal(eligible, await hashes.SaveObservedAsync(new() { Path = Original, SizeBytes = 42, FileModifiedAt = current.FileModifiedAt, Hash = "new" }, current));
    }

    [Fact] public async Task ChangedFingerprintInvalidatesCaptureAndBothDerivedCaches()
    {
        var store = await CreateAsync(); var hashes = new DuplicateHashStore(_root); await hashes.InitializeAsync();
        var legacy = new MetadataIndexStore(_root); await legacy.InitializeAsync();
        var before = (await store.GetItemAsync(Original))!;
        await hashes.SaveObservedAsync(new() { Path = Original, SizeBytes = 42, FileModifiedAt = before.FileModifiedAt, Hash = "old" }, before);
        await legacy.SaveAsync(Original, 42, Modified, Capture);
        var changed = Observed(); changed.SizeBytes = 43;
        Assert.True(await store.ApplyObservationAsync(new(changed), before));
        var after = (await store.GetItemAsync(Original))!;
        Assert.Null(after.CaptureDate); Assert.False(after.MetadataIndexed); Assert.True(after.IsFavorite);
        Assert.Null(await hashes.TryGetAsync(Original, 42, before.FileModifiedAt));
        Assert.Empty(await legacy.LoadCaptureDatesAsync());
    }

    [Fact] public async Task AvailableReprobeRestoresStatusAndMissingObservationCannotInsertPhantomRows()
    {
        var store = await CreateAsync(); var before = (await store.GetItemAsync(Original))!;
        await store.SetAvailabilityAsync(before, FileAvailability.Missing, Checked.AddMinutes(1));
        var missing = (await store.GetItemAsync(Original))!;
        Assert.False(await store.ApplyObservationAsync(new(Observed()), missing)); // older probe cannot overwrite a newer check
        var restored = Observed(); restored.AvailabilityCheckedAtUtc = Checked.AddMinutes(2);
        Assert.True(await store.ApplyObservationAsync(new(restored), missing));
        Assert.Equal(FileAvailability.Available, (await store.GetItemAsync(Original))!.Availability);
        var phantom = new SavedMediaItem { Path = @"D:\Photos\unknown.jpg", Availability = FileAvailability.Missing, AvailabilityCheckedAtUtc = Checked };
        Assert.False(await store.ApplyObservationAsync(new(phantom)));
        Assert.Null(await store.GetItemAsync(phantom.Path));
        Assert.False(await store.ApplyObservationAsync(new(Observed()))); // insert-only race
    }

    [Fact] public async Task ExternalRenameRetainsAssetAndUserDataWithoutCreatingOperationReceipt()
    {
        var store = await CreateAsync(); var before = (await store.GetItemAsync(Original))!;
        var hashes = new DuplicateHashStore(_root); await hashes.InitializeAsync();
        Assert.True(await hashes.SaveObservedAsync(new() { Path = Original, SizeBytes = 42, FileModifiedAt = before.FileModifiedAt, Hash = "stable-hash" }, before));
        var destination = Observed(@"D:\Albums\renamed.jpg");
        Assert.True(await store.TryReconcileExternalRenameAsync(before, destination));
        Assert.Null(await store.GetItemAsync(Original));
        var after = (await store.GetItemAsync(destination.Path))!;
        Assert.Equal(before.AssetId, after.AssetId); Assert.True(after.IsFavorite); Assert.Equal(Capture, after.CaptureDate);
        Assert.Equal(before.FileIdentity, after.FileIdentity); Assert.True(after.MetadataIndexed);
        Assert.Equal("stable-hash", (await hashes.TryGetAsync(destination.Path, 42, after.FileModifiedAt))!.Hash);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "catalog-v2.sqlite")}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM desktop_move_receipts;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact] public async Task ExternalRenameRefusesAmbiguousIdentityOccupiedDestinationChangedFileAndStaleSource()
    {
        var store = await CreateAsync(); var before = (await store.GetItemAsync(Original))!;
        var destination = Observed(@"D:\Albums\a.jpg");
        destination.SizeBytes++;
        Assert.False(await store.TryReconcileExternalRenameAsync(before, destination));
        destination.SizeBytes--;
        await store.UpsertItemsAsync([Observed(destination.Path, "another-file")]);
        Assert.False(await store.TryReconcileExternalRenameAsync(before, destination));
        await store.UpsertItemsAsync([Observed(@"D:\Photos\hardlink.jpg")]);
        Assert.Equal(2, (await store.FindByFileIdentityAsync(before.FileIdentity!)).Count);
        Assert.False(await store.TryReconcileExternalRenameAsync(before, Observed(@"D:\Albums\free.jpg")));
        await store.SetAvailabilityAsync(before, FileAvailability.Missing, Checked.AddMinutes(1));
        Assert.False(await store.TryReconcileExternalRenameAsync(before, Observed(@"D:\Albums\free.jpg")));
        Assert.True((await store.GetItemAsync(Original))!.IsFavorite);
    }

    [Fact] public async Task MoveCallbackClearsOsIdentityAndOldObservationCannotResurrectQuarantine()
    {
        var store = await CreateAsync(); var before = (await store.GetItemAsync(Original))!;
        const string destination = @"E:\Quarantine\a.jpg";
        await store.MoveItemAsync(Original, destination, removeFromLibrary: true);
        var moved = (await store.GetItemAsync(destination))!;
        Assert.Null(moved.FileIdentity); Assert.Equal(FileAvailability.NeedsVerification, moved.Availability);
        Assert.False(await store.ApplyObservationAsync(new(Observed()), before));
        Assert.False(await store.ApplyObservationAsync(new(Observed(destination)), moved));
        Assert.Equal(0, await store.CountAsync(new()));
    }

    [Fact] public async Task SubtreeUpdatesAreBoundedPreserveNewerObservationsAndDoNotTouchNeighborPrefix()
    {
        var store = await CreateAsync();
        await store.UpsertItemsAsync(Enumerable.Range(0, 600).Select(i => Observed($@"D:\Photos\nested\{i:D4}.jpg", $"id-{i}")));
        await store.UpsertItemsAsync([Observed(@"D:\Photos-other\safe.jpg")]);
        var recent = Observed(@"D:\Photos\newer.jpg"); recent.AvailabilityCheckedAtUtc = Checked.AddMinutes(2);
        await store.UpsertItemsAsync([recent]);
        var total = await store.SetSubtreeAvailabilityAsync(@"D:\Photos", FileAvailability.RootOffline, Checked.AddMinutes(1), "offline");
        Assert.Equal(601, total);
        Assert.Equal(FileAvailability.Available, (await store.GetItemAsync(recent.Path))!.Availability);
        Assert.Equal(FileAvailability.Available, (await store.GetItemAsync(@"D:\Photos-other\safe.jpg"))!.Availability);
        var seen = new HashSet<string>(); string? after = null;
        while (true)
        {
            var page = await store.QuerySubtreePageAsync(@"D:\Photos", after, 127);
            if (page.Count == 0) break;
            foreach (var item in page) Assert.True(seen.Add(item.Path));
            after = page[^1].Path;
        }
        Assert.Equal(602, seen.Count);
    }

    [Fact] public async Task BatchIsAtomicOnInvalidObservationAndPreservesOrdering()
    {
        var store = await CreateAsync(); var before = (await store.GetItemAsync(Original))!;
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyObservationsAsync([
            (new(Observed(), true), before), (new(new SavedMediaItem { Path = "bad.jpg" }), null)]));
        Assert.Equal(before.ObservationVersion, (await store.GetItemAsync(Original))!.ObservationVersion);
        var result = await store.ApplyObservationsAsync([(new(Observed(), true), before), (new(Observed(), true), before), (new(Observed(@"D:\Photos\new.jpg")), null)]);
        Assert.Equal(new[] { true, false, true }, result);
        var found = await store.GetItemsByPathsAsync([Original, @"D:\Photos\new.jpg", "missing"]);
        Assert.Equal(2, found.Count);
    }

    [Fact] public async Task KnownFoldersAndWatchSettingsArePersistedWithoutEnumeratingFiles()
    {
        var store = await CreateAsync();
        await store.UpsertItemsAsync([Observed(@"D:\Photos\sub\a.jpg"), Observed(@"D:\Photos\sub\b.jpg"), Observed(@"C:\at-root.jpg")]);
        var first = Assert.Single(await store.QueryKnownFoldersPageAsync(pageSize: 1)); Assert.Equal(@"C:\", first);
        var remaining = await store.QueryKnownFoldersPageAsync(first);
        Assert.Equal(new[] { @"D:\Photos", @"D:\Photos\sub" }, remaining);
        await store.SaveAsync(new() { WatchedFolders = [@"D:\Photos"] }, saveItems: false);
        Assert.Equal(new[] { @"D:\Photos" }, (await store.LoadAsync(includeItems: false)).WatchedFolders);
    }

    [Fact] public async Task ObservationMigrationCreatesVerifiedBackupAndPreservesUnverifiedLegacyRows()
    {
        var store = await CreateAsync();
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "catalog-v2.sqlite")}"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX ix_desktop_file_identity;
                ALTER TABLE desktop_media_items DROP COLUMN availability;
                ALTER TABLE desktop_media_items DROP COLUMN availability_checked_ticks;
                ALTER TABLE desktop_media_items DROP COLUMN availability_error_code;
                ALTER TABLE desktop_media_items DROP COLUMN file_identity;
                ALTER TABLE desktop_media_items DROP COLUMN observation_version;
                """; command.ExecuteNonQuery();
        }
        await store.InitializeAsync(); await store.InitializeAsync();
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(_root, "backups"), "catalog-before-file-observations-*.sqlite"));
        var item = (await store.GetItemAsync(Original))!;
        Assert.Equal(FileAvailability.NeedsVerification, item.Availability); Assert.True(item.IsFavorite); Assert.Equal(Capture, item.CaptureDate);
        using var backupConnection = new SqliteConnection($"Data Source={backup};Mode=ReadOnly"); backupConnection.Open();
        using var check = backupConnection.CreateCommand(); check.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('desktop_media_items') WHERE name='availability';";
        Assert.Equal(0L, check.ExecuteScalar());
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
