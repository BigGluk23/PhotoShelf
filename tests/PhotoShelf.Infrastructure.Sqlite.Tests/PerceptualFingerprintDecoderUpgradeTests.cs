using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Duplicates;
using PhotoShelf.Infrastructure.Sqlite;
using Xunit;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class PerceptualFingerprintDecoderUpgradeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("photoshelf-codec-upgrade-").FullName;
    private static readonly PerceptualFingerprint Fingerprint = new(1, 0x1234, 0x5678, 32, 24);

    [Fact]
    public async Task OldSchemaRetriesOnlyFailedWebpAndPreservesHealthyCacheCatalogAndBytes()
    {
        var catalog = new SqliteDesktopCatalogStore(_root); await catalog.InitializeAsync();
        var store = new PerceptualFingerprintStore(_root); await store.InitializeAsync();
        var failed = await SeedAsync(catalog, "legacy.WEBP", favorite: true);
        var healthy = await SeedAsync(catalog, "healthy.webp");
        var png = await SeedAsync(catalog, "unsupported.png");
        var corrupt = await SeedAsync(catalog, "corrupt.webp");
        var transient = await SeedAsync(catalog, "locked.webp");
        var otherError = await SeedAsync(catalog, "other.webp");
        var now = DateTime.UtcNow;
        Assert.All(await store.SaveObservedBatchAsync([
            new(failed, PerceptualFingerprintReadResult.Unsupported("NotSupportedException"), now),
            new(healthy, PerceptualFingerprintReadResult.Found(Fingerprint), now),
            new(png, PerceptualFingerprintReadResult.Unsupported("NotSupportedException"), now),
            new(corrupt, PerceptualFingerprintReadResult.Corrupt("FileFormatException"), now),
            new(transient, PerceptualFingerprintReadResult.Transient("IOException", now.AddHours(1)), now),
            new(otherError, PerceptualFingerprintReadResult.Unsupported("other-codec"), now)]), Assert.True);
        // Restore the actual previous schema, without touching any authoritative tables.
        await ExecuteAsync("ALTER TABLE perceptual_fingerprint_cache DROP COLUMN decoder_revision;");
        var catalogBefore = await SnapshotAsync("desktop_media_items");
        var bytes = File.ReadAllBytes(failed.Path); var hash = SHA256.HashData(bytes);
        var modified = File.GetLastWriteTimeUtc(failed.Path);
        var upgraded = new PerceptualFingerprintStore(_root); await upgraded.InitializeAsync();
        var healthyBefore = await SnapshotAsync("perceptual_fingerprint_cache", healthy.AssetId);
        Assert.Equal(catalogBefore, await SnapshotAsync("desktop_media_items"));
        var scope = new PerceptualFingerprintScope([], [], true, now);
        Assert.Equal(failed.AssetId, Assert.Single(await upgraded.QueryDuePageAsync(scope)).AssetId);

        Assert.True(Assert.Single(await upgraded.SaveObservedBatchAsync([
            new(failed, PerceptualFingerprintReadResult.Found(Fingerprint), now)])));
        await new PerceptualFingerprintStore(_root).InitializeAsync();
        Assert.Empty(await upgraded.QueryDuePageAsync(scope));
        Assert.Equal(healthyBefore, await SnapshotAsync("perceptual_fingerprint_cache", healthy.AssetId));
        Assert.Equal(catalogBefore, await SnapshotAsync("desktop_media_items"));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(failed.Path)));
        Assert.Equal(bytes.Length, new FileInfo(failed.Path).Length);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(failed.Path));
        Assert.True((await catalog.GetItemAsync(failed.Path))!.IsFavorite);
        await AssertIntegrityAsync();
    }

    [Fact]
    public async Task RejectedWebpIsRetriedAtMostOnceWithCurrentDecoder()
    {
        var catalog = new SqliteDesktopCatalogStore(_root); await catalog.InitializeAsync();
        var item = await SeedAsync(catalog, "still-unsupported.webp");
        var store = new PerceptualFingerprintStore(_root); await store.InitializeAsync();
        Assert.True(Assert.Single(await store.SaveObservedBatchAsync([
            new(item, PerceptualFingerprintReadResult.Unsupported("NotSupportedException"), DateTime.UtcNow)])));
        await ExecuteAsync("UPDATE perceptual_fingerprint_cache SET decoder_revision=0;");
        var scope = new PerceptualFingerprintScope([], [], true, DateTime.UtcNow);
        Assert.Equal(item.AssetId, Assert.Single(await store.QueryDuePageAsync(scope)).AssetId);

        Assert.True(Assert.Single(await store.SaveObservedBatchAsync([
            new(item, PerceptualFingerprintReadResult.Unsupported("NotSupportedException"), DateTime.UtcNow)])));
        var restarted = new PerceptualFingerprintStore(_root); await restarted.InitializeAsync();
        Assert.Empty(await restarted.QueryDuePageAsync(scope));
        await AssertIntegrityAsync();
    }

    [Fact]
    public async Task CancelledOrFailedCommitKeepsLegacyRetryAndCatalogUsable()
    {
        var catalog = new SqliteDesktopCatalogStore(_root); await catalog.InitializeAsync();
        var item = await SeedAsync(catalog, "retry.webp", favorite: true);
        var store = new PerceptualFingerprintStore(_root); await store.InitializeAsync();
        Assert.True(Assert.Single(await store.SaveObservedBatchAsync([
            new(item, PerceptualFingerprintReadResult.Unsupported("NotSupportedException"), DateTime.UtcNow)])));
        await ExecuteAsync("UPDATE perceptual_fingerprint_cache SET decoder_revision=0;");
        var catalogBefore = await SnapshotAsync("desktop_media_items");
        var cacheBefore = await SnapshotAsync("perceptual_fingerprint_cache");
        var update = new PerceptualFingerprintCatalogUpdate(item, PerceptualFingerprintReadResult.Found(Fingerprint), DateTime.UtcNow);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveObservedBatchAsync([update], cancelled.Token));
        // Fail after the cache row/revision changed but before its band insert completes.
        // The entire transaction must restore the previous retry, not acknowledge it early.
        await ExecuteAsync("CREATE TRIGGER synthetic_commit_failure BEFORE INSERT ON perceptual_fingerprint_bands BEGIN SELECT RAISE(ABORT,'synthetic checkpoint'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => store.SaveObservedBatchAsync([update]));
        Assert.Equal(cacheBefore, await SnapshotAsync("perceptual_fingerprint_cache"));
        Assert.Equal(catalogBefore, await SnapshotAsync("desktop_media_items"));
        Assert.Equal(item.AssetId, Assert.Single(await store.QueryDuePageAsync(new([], [], true, DateTime.UtcNow))).AssetId);
        await ExecuteAsync("DROP TRIGGER synthetic_commit_failure;");
        Assert.True(Assert.Single(await store.SaveObservedBatchAsync([update])));
        Assert.Equal(item.AssetId, Assert.Single(await store.FindCandidatesAsync(Fingerprint)).AssetId);
        Assert.Equal(catalogBefore, await SnapshotAsync("desktop_media_items"));
        await AssertIntegrityAsync();
    }

    private async Task<SavedMediaItem> SeedAsync(SqliteDesktopCatalogStore catalog, string name, bool favorite = false)
    {
        var path = Path.Combine(_root, name); File.WriteAllBytes(path, [1, 2, 3, 4]);
        await catalog.UpsertItemsAsync([new SavedMediaItem
        {
            AssetId = Guid.NewGuid().ToString("N"), Path = path, SizeBytes = 4,
            FileModifiedAt = File.GetLastWriteTime(path), ObservationVersion = 1,
            Availability = FileAvailability.Available, IsFavorite = favorite
        }]);
        return (await catalog.GetItemAsync(path))!;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "catalog-v2.sqlite")};Pooling=False");
        await connection.OpenAsync(); return connection;
    }
    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private async Task<string> SnapshotAsync(string table, string? assetId = null)
    {
        await using var connection = await OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table}" + (assetId is null ? "" : " WHERE asset_id=$id") + " ORDER BY asset_id;";
        if (assetId is not null) command.Parameters.AddWithValue("$id", assetId);
        var rows = new List<object?[]>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
        return JsonSerializer.Serialize(rows);
    }
    private async Task AssertIntegrityAsync()
    {
        await using var connection = await OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;"; Assert.Equal("ok", await command.ExecuteScalarAsync());
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); Directory.Delete(_root, true);
    }
}
