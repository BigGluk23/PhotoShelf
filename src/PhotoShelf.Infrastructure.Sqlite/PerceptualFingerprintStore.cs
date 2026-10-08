using System.Numerics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Duplicates;

namespace PhotoShelf.Infrastructure.Sqlite;

public sealed record PerceptualFingerprintScope(
    IReadOnlyList<string> IncludedFolders,
    IReadOnlyList<string> ExcludedFolders,
    bool IncludeSystemFolders,
    DateTime DueAtUtc);

public sealed record PerceptualFingerprintCatalogUpdate(
    SavedMediaItem Expected,
    PerceptualFingerprintReadResult Result,
    DateTime AttemptedAtUtc);

public sealed record SavedPerceptualFingerprint(
    string AssetId,
    string Path,
    long SizeBytes,
    DateTime? FileModifiedAt,
    long ObservationVersion,
    PerceptualFingerprint Fingerprint,
    int DifferenceDistance = 0,
    int AverageDistance = 0);

public sealed record ObservedPerceptualFingerprint(
    SavedMediaItem Item,
    PerceptualFingerprint Fingerprint);

/// <summary>
/// Data-access boundary for the derived visual-similarity index. The cache can be deleted and
/// rebuilt; original media and authoritative catalog observations are never changed here.
/// </summary>
public sealed class PerceptualFingerprintStore
{
    private readonly string _connectionString;
    private readonly string _directory;

    public PerceptualFingerprintStore(string? catalogDirectory = null)
    {
        _directory = catalogDirectory ?? LocalCatalogStore.CatalogDirectory;
        _connectionString = CatalogDatabaseAccess.ConnectionString(_directory);
    }

    public Task InitializeAsync(CancellationToken token = default) => Task.Run(() =>
        CatalogDatabaseAccess.WriteAsync(_directory, async () =>
        {
            Directory.CreateDirectory(_directory);
            await using var connection = await OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS perceptual_fingerprint_cache (
                    asset_id TEXT NOT NULL PRIMARY KEY,
                    path TEXT NOT NULL,
                    path_key TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    file_modified_utc_ticks INTEGER NOT NULL,
                    observation_version INTEGER NOT NULL,
                    algorithm_version INTEGER NOT NULL,
                    status INTEGER NOT NULL,
                    difference_hash INTEGER NULL,
                    average_hash INTEGER NULL,
                    pixel_width INTEGER NULL,
                    pixel_height INTEGER NULL,
                    attempted_at_utc_ticks INTEGER NOT NULL,
                    retry_at_utc_ticks INTEGER NULL,
                    error_code TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_perceptual_fingerprint_path ON perceptual_fingerprint_cache(path_key);
                CREATE TABLE IF NOT EXISTS perceptual_fingerprint_bands (
                    asset_id TEXT NOT NULL,
                    algorithm_version INTEGER NOT NULL,
                    band_index INTEGER NOT NULL,
                    band_value INTEGER NOT NULL,
                    PRIMARY KEY(asset_id,algorithm_version,band_index)
                );
                CREATE INDEX IF NOT EXISTS ix_perceptual_fingerprint_band
                    ON perceptual_fingerprint_bands(algorithm_version,band_index,band_value,asset_id);
                """;
            await command.ExecuteNonQueryAsync(token);
        }, token), token);

    /// <summary>
    /// Reads one stable keyset page of missing/stale work. A fixed DueAtUtc keeps retries that
    /// become eligible during a pass for the next pass instead of reshuffling the current one.
    /// </summary>
    public Task<IReadOnlyList<SavedMediaItem>> QueryDuePageAsync(PerceptualFingerprintScope scope,
        string? afterPath = null, int pageSize = 64, CancellationToken token = default) =>
        Task.Run<IReadOnlyList<SavedMediaItem>>(async () =>
        {
            if (pageSize is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(pageSize));
            await using var connection = await OpenAsync(token);
            using var interrupt = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
            await using var command = connection.CreateCommand();
            var folderPredicate = BuildFolderPredicate(scope, command, token);
            command.CommandText = $"""
                SELECT i.asset_id,i.path,i.is_favorite,i.size_bytes,i.file_modified_utc_ticks,i.is_video,
                    i.capture_date_ticks,i.metadata_indexed,i.is_hidden_or_system,i.file_local_ticks,
                    i.metadata_status,i.metadata_attempted_ticks,i.metadata_retry_ticks,i.metadata_error_code,
                    i.availability,i.availability_checked_ticks,i.availability_error_code,i.file_identity,i.observation_version
                FROM desktop_media_items AS i
                LEFT JOIN perceptual_fingerprint_cache AS p
                  ON p.asset_id=i.asset_id AND p.path_key=i.path_key AND p.size_bytes=i.size_bytes
                 AND p.file_modified_utc_ticks=i.file_modified_utc_ticks
                 AND p.observation_version=i.observation_version AND p.algorithm_version=$algorithm
                WHERE i.is_quarantined=0 AND i.is_video=0
                  AND (i.availability=0 OR (i.availability=4 AND i.availability_error_code IS NULL))
                  AND ($includeSystem=1 OR i.is_hidden_or_system=0)
                  AND i.path_key>$after AND {folderPredicate}
                  AND (p.asset_id IS NULL OR (p.status=$transient AND p.retry_at_utc_ticks<=$due))
                ORDER BY i.path_key ASC LIMIT $take;
                """;
            command.Parameters.AddWithValue("$algorithm", PerceptualFingerprint.CurrentAlgorithmVersion);
            command.Parameters.AddWithValue("$includeSystem", scope.IncludeSystemFolders ? 1 : 0);
            command.Parameters.AddWithValue("$after", afterPath is null ? "" : SqliteDesktopCatalogStore.NormalizePathKey(afterPath));
            command.Parameters.AddWithValue("$transient", (int)PerceptualFingerprintStatus.TransientError);
            command.Parameters.AddWithValue("$due", scope.DueAtUtc.ToUniversalTime().Ticks);
            command.Parameters.AddWithValue("$take", pageSize);
            try
            {
                var result = new List<SavedMediaItem>(pageSize);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) result.Add(ReadItem(reader));
                return result;
            }
            catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        }, token);

    /// <summary>
    /// Commits only results whose asset/path/size/mtime/observation snapshot is still current.
    /// Returns one boolean per input so callers never publish a rejected stale result.
    /// </summary>
    public Task<IReadOnlyList<bool>> SaveObservedBatchAsync(IReadOnlyList<PerceptualFingerprintCatalogUpdate> updates,
        CancellationToken token = default)
    {
        if (updates.Count > 64) throw new ArgumentOutOfRangeException(nameof(updates), "Fingerprint batches are limited to 64 outcomes.");
        var snapshots = updates.Select(update => new PerceptualFingerprintCatalogUpdate(new SavedMediaItem
        {
            AssetId = update.Expected.AssetId,
            Path = update.Expected.Path,
            SizeBytes = update.Expected.SizeBytes,
            FileModifiedAt = update.Expected.FileModifiedAt,
            ObservationVersion = update.Expected.ObservationVersion
        }, update.Result, update.AttemptedAtUtc)).ToArray();
        return Task.Run<IReadOnlyList<bool>>(async () =>
        {
            if (snapshots.Length == 0) return Array.Empty<bool>();
            IReadOnlyList<bool> committed = Array.Empty<bool>();
            await CatalogDatabaseAccess.WriteAsync(_directory, async () =>
            {
                await using var connection = await OpenAsync(token);
                committed = await SaveCoreAsync(connection, snapshots, token);
            }, token);
            return committed;
        }, token);
    }

    /// <summary>
    /// Uses four indexed 16-bit difference-hash bands, then verifies true Hamming distance.
    /// A threshold up to three is complete: at least one band must remain unchanged.
    /// </summary>
    public Task<IReadOnlyList<SavedPerceptualFingerprint>> FindCandidatesAsync(PerceptualFingerprint fingerprint,
        string? exceptAssetId = null, int maximumDifferenceDistance = 3, int limit = 256,
        CancellationToken token = default) => Task.Run<IReadOnlyList<SavedPerceptualFingerprint>>(async () =>
    {
        if (fingerprint.AlgorithmVersion != PerceptualFingerprint.CurrentAlgorithmVersion)
            throw new ArgumentException("Only the current fingerprint algorithm is queryable.", nameof(fingerprint));
        if (maximumDifferenceDistance is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(maximumDifferenceDistance));
        if (limit is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(token);
        using var interrupt = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        connection.CreateFunction<long, long, int>("ps_hamming", (left, right) =>
            BitOperations.PopCount(unchecked((ulong)left) ^ unchecked((ulong)right)));
        await using var command = connection.CreateCommand();
        var bands = Enumerable.Range(0, PerceptualFingerprint.BandCount)
            .Select(index => $"(b.band_index={index} AND b.band_value=$band{index})");
        command.CommandText = $"""
            SELECT DISTINCT p.asset_id,p.path,p.size_bytes,p.file_modified_utc_ticks,p.observation_version,
                p.difference_hash,p.average_hash,p.pixel_width,p.pixel_height,
                ps_hamming(p.difference_hash,$difference) AS difference_distance,
                ps_hamming(p.average_hash,$average) AS average_distance
            FROM perceptual_fingerprint_bands AS b
            JOIN perceptual_fingerprint_cache AS p ON p.asset_id=b.asset_id AND p.algorithm_version=b.algorithm_version
            JOIN desktop_media_items AS i ON i.asset_id=p.asset_id AND i.path_key=p.path_key
                AND i.size_bytes=p.size_bytes AND i.file_modified_utc_ticks=p.file_modified_utc_ticks
                AND i.observation_version=p.observation_version
            WHERE b.algorithm_version=$algorithm AND ({string.Join(" OR ", bands)})
                AND p.status=$found AND i.is_quarantined=0 AND i.is_video=0
                AND ($except IS NULL OR p.asset_id<>$except)
                AND ps_hamming(p.difference_hash,$difference)<=$maximumDistance
            ORDER BY difference_distance,average_distance,p.path LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$algorithm", fingerprint.AlgorithmVersion);
        for (var index = 0; index < PerceptualFingerprint.BandCount; index++)
            command.Parameters.AddWithValue($"$band{index}", fingerprint.DifferenceBand(index));
        command.Parameters.AddWithValue("$found", (int)PerceptualFingerprintStatus.Found);
        command.Parameters.AddWithValue("$except", (object?)exceptAssetId ?? DBNull.Value);
        command.Parameters.AddWithValue("$difference", unchecked((long)fingerprint.DifferenceHash));
        command.Parameters.AddWithValue("$average", unchecked((long)fingerprint.AverageHash));
        command.Parameters.AddWithValue("$maximumDistance", maximumDifferenceDistance);
        command.Parameters.AddWithValue("$limit", limit);
        var candidates = new List<SavedPerceptualFingerprint>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var candidateFingerprint = new PerceptualFingerprint(fingerprint.AlgorithmVersion,
                    unchecked((ulong)reader.GetInt64(5)), unchecked((ulong)reader.GetInt64(6)),
                    reader.GetInt32(7), reader.GetInt32(8));
                candidates.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                    reader.GetInt64(3) == 0 ? null : new DateTime(reader.GetInt64(3), DateTimeKind.Utc).ToLocalTime(),
                    reader.GetInt64(4), candidateFingerprint, reader.GetInt32(9), reader.GetInt32(10)));
            }
            return candidates;
        }
        catch (SqliteException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }, token);

    /// <summary>
    /// Reads only fingerprints that still belong to the supplied catalog observations. This is
    /// the bridge between an arbitrary paged catalog view and a disk-backed similarity session;
    /// stale derived rows are never allowed into a review snapshot.
    /// </summary>
    public Task<IReadOnlyList<ObservedPerceptualFingerprint>> ReadObservedBatchAsync(
        IReadOnlyList<SavedMediaItem> expected, CancellationToken token = default) =>
        Task.Run<IReadOnlyList<ObservedPerceptualFingerprint>>(async () =>
        {
            if (expected.Count > 128)
                throw new ArgumentOutOfRangeException(nameof(expected), "Fingerprint reads are limited to 128 observations.");
            if (expected.Count == 0) return Array.Empty<ObservedPerceptualFingerprint>();

            await using var connection = await OpenAsync(token);
            using var interrupt = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
            await using var command = connection.CreateCommand();
            var ids = new string[expected.Count];
            for (var index = 0; index < expected.Count; index++)
            {
                ids[index] = $"$id{index}";
                command.Parameters.AddWithValue(ids[index], expected[index].AssetId);
            }
            command.CommandText = $"""
                SELECT i.asset_id,i.path,i.is_favorite,i.size_bytes,i.file_modified_utc_ticks,i.is_video,
                    i.capture_date_ticks,i.metadata_indexed,i.is_hidden_or_system,i.file_local_ticks,
                    i.metadata_status,i.metadata_attempted_ticks,i.metadata_retry_ticks,i.metadata_error_code,
                    i.availability,i.availability_checked_ticks,i.availability_error_code,i.file_identity,i.observation_version,
                    p.difference_hash,p.average_hash,p.pixel_width,p.pixel_height
                FROM desktop_media_items AS i
                JOIN perceptual_fingerprint_cache AS p ON p.asset_id=i.asset_id AND p.path_key=i.path_key
                    AND p.size_bytes=i.size_bytes AND p.file_modified_utc_ticks=i.file_modified_utc_ticks
                    AND p.observation_version=i.observation_version
                WHERE i.asset_id IN ({string.Join(',', ids)}) AND i.is_quarantined=0 AND i.is_video=0
                    AND p.algorithm_version=$algorithm AND p.status=$found
                ORDER BY i.path_key;
                """;
            command.Parameters.AddWithValue("$algorithm", PerceptualFingerprint.CurrentAlgorithmVersion);
            command.Parameters.AddWithValue("$found", (int)PerceptualFingerprintStatus.Found);

            var byId = expected.ToDictionary(item => item.AssetId, StringComparer.Ordinal);
            var result = new List<ObservedPerceptualFingerprint>(expected.Count);
            try
            {
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var item = ReadItem(reader);
                    if (!byId.TryGetValue(item.AssetId, out var snapshot) ||
                        snapshot.ObservationVersion != item.ObservationVersion ||
                        snapshot.SizeBytes != item.SizeBytes ||
                        snapshot.FileModifiedAt?.ToUniversalTime().Ticks != item.FileModifiedAt?.ToUniversalTime().Ticks)
                        continue;
                    var fingerprint = new PerceptualFingerprint(
                        PerceptualFingerprint.CurrentAlgorithmVersion,
                        unchecked((ulong)reader.GetInt64(19)), unchecked((ulong)reader.GetInt64(20)),
                        reader.GetInt32(21), reader.GetInt32(22));
                    result.Add(new(item, fingerprint));
                }
                return result;
            }
            catch (SqliteException) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
        }, token);

    private static string BuildFolderPredicate(PerceptualFingerprintScope scope, SqliteCommand command, CancellationToken token)
    {
        if (scope.ExcludedFolders.Count == 0) return "1=1";
        var ranges = FolderRuleIntervals.Compile(scope.IncludedFolders, scope.ExcludedFolders, token);
        command.Parameters.AddWithValue("$fingerprintRanges", JsonSerializer.Serialize(ranges.Bounded));
        command.Parameters.AddWithValue("$fingerprintTail", ranges.Tail);
        return "(i.path_key>=$fingerprintTail OR EXISTS (SELECT 1 FROM json_each($fingerprintRanges) AS ranges " +
            "WHERE i.path_key>=json_extract(ranges.value,'$[0]') AND i.path_key<json_extract(ranges.value,'$[1]')))";
    }

    private static async Task<IReadOnlyList<bool>> SaveCoreAsync(SqliteConnection connection,
        IReadOnlyList<PerceptualFingerprintCatalogUpdate> updates, CancellationToken token)
    {
        await using var transaction = connection.BeginTransaction();
        var results = new bool[updates.Count];
        for (var index = 0; index < updates.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var update = updates[index];
            var expected = update.Expected;
            var result = update.Result;
            var fingerprint = result.Fingerprint;
            if (result.Status == PerceptualFingerprintStatus.Found &&
                (fingerprint is null || fingerprint.AlgorithmVersion != PerceptualFingerprint.CurrentAlgorithmVersion))
                continue;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO perceptual_fingerprint_cache(asset_id,path,path_key,size_bytes,file_modified_utc_ticks,
                    observation_version,algorithm_version,status,difference_hash,average_hash,pixel_width,pixel_height,
                    attempted_at_utc_ticks,retry_at_utc_ticks,error_code)
                SELECT $id,$path,$key,$size,$modified,$version,$algorithm,$status,$difference,$average,$width,$height,
                    $attempted,$retry,$error
                WHERE EXISTS(SELECT 1 FROM desktop_media_items WHERE asset_id=$id AND path_key=$key
                    AND size_bytes=$size AND file_modified_utc_ticks=$modified AND observation_version=$version
                    AND is_quarantined=0 AND is_video=0
                    AND (availability=0 OR (availability=4 AND availability_error_code IS NULL)))
                ON CONFLICT(asset_id) DO UPDATE SET path=excluded.path,path_key=excluded.path_key,
                    size_bytes=excluded.size_bytes,file_modified_utc_ticks=excluded.file_modified_utc_ticks,
                    observation_version=excluded.observation_version,algorithm_version=excluded.algorithm_version,
                    status=excluded.status,difference_hash=excluded.difference_hash,average_hash=excluded.average_hash,
                    pixel_width=excluded.pixel_width,pixel_height=excluded.pixel_height,
                    attempted_at_utc_ticks=excluded.attempted_at_utc_ticks,retry_at_utc_ticks=excluded.retry_at_utc_ticks,
                    error_code=excluded.error_code;
                """;
            command.Parameters.AddWithValue("$id", expected.AssetId);
            command.Parameters.AddWithValue("$path", expected.Path);
            command.Parameters.AddWithValue("$key", SqliteDesktopCatalogStore.NormalizePathKey(expected.Path));
            command.Parameters.AddWithValue("$size", expected.SizeBytes);
            command.Parameters.AddWithValue("$modified", expected.FileModifiedAt?.ToUniversalTime().Ticks ?? 0);
            command.Parameters.AddWithValue("$version", expected.ObservationVersion);
            command.Parameters.AddWithValue("$algorithm", PerceptualFingerprint.CurrentAlgorithmVersion);
            command.Parameters.AddWithValue("$status", (int)result.Status);
            command.Parameters.AddWithValue("$difference", fingerprint is null ? DBNull.Value : unchecked((long)fingerprint.DifferenceHash));
            command.Parameters.AddWithValue("$average", fingerprint is null ? DBNull.Value : unchecked((long)fingerprint.AverageHash));
            command.Parameters.AddWithValue("$width", fingerprint is null ? DBNull.Value : fingerprint.PixelWidth);
            command.Parameters.AddWithValue("$height", fingerprint is null ? DBNull.Value : fingerprint.PixelHeight);
            command.Parameters.AddWithValue("$attempted", update.AttemptedAtUtc.ToUniversalTime().Ticks);
            command.Parameters.AddWithValue("$retry", (object?)result.RetryAtUtc?.ToUniversalTime().Ticks ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)result.ErrorCode ?? DBNull.Value);
            if (await command.ExecuteNonQueryAsync(token) != 1) continue;
            results[index] = true;

            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM perceptual_fingerprint_bands WHERE asset_id=$id;";
            delete.Parameters.AddWithValue("$id", expected.AssetId);
            await delete.ExecuteNonQueryAsync(token);
            if (fingerprint is null) continue;
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO perceptual_fingerprint_bands(asset_id,algorithm_version,band_index,band_value) VALUES($id,$algorithm,$index,$value);";
            insert.Parameters.AddWithValue("$id", expected.AssetId);
            insert.Parameters.AddWithValue("$algorithm", fingerprint.AlgorithmVersion);
            var indexParameter = insert.Parameters.Add("$index", SqliteType.Integer);
            var valueParameter = insert.Parameters.Add("$value", SqliteType.Integer);
            for (var band = 0; band < PerceptualFingerprint.BandCount; band++)
            {
                indexParameter.Value = band;
                valueParameter.Value = fingerprint.DifferenceBand(band);
                await insert.ExecuteNonQueryAsync(token);
            }
        }
        await transaction.CommitAsync(token);
        return results;
    }

    private static SavedMediaItem ReadItem(SqliteDataReader reader) => new()
    {
        AssetId = reader.GetString(0), Path = reader.GetString(1), IsFavorite = reader.GetInt64(2) == 1,
        SizeBytes = reader.GetInt64(3),
        FileModifiedAt = reader.GetInt64(4) == 0 ? null : new DateTime(reader.GetInt64(4), DateTimeKind.Utc).ToLocalTime(),
        IsVideo = reader.GetInt64(5) == 1,
        CaptureDate = reader.IsDBNull(6) ? null : new DateTime(reader.GetInt64(6), DateTimeKind.Unspecified),
        MetadataIndexed = reader.GetInt64(7) == 1, IsHiddenOrSystem = reader.GetInt64(8) == 1,
        MetadataStatus = (PhotoShelf.Application.Metadata.MetadataReadStatus)reader.GetInt32(10),
        MetadataAttemptedAtUtc = reader.IsDBNull(11) ? null : new DateTime(reader.GetInt64(11), DateTimeKind.Utc),
        MetadataRetryAtUtc = reader.IsDBNull(12) ? null : new DateTime(reader.GetInt64(12), DateTimeKind.Utc),
        MetadataErrorCode = reader.IsDBNull(13) ? null : reader.GetString(13),
        Availability = (FileAvailability)reader.GetInt32(14),
        AvailabilityCheckedAtUtc = reader.IsDBNull(15) ? null : new DateTime(reader.GetInt64(15), DateTimeKind.Utc),
        AvailabilityErrorCode = reader.IsDBNull(16) ? null : reader.GetString(16),
        FileIdentity = reader.IsDBNull(17) ? null : reader.GetString(17), ObservationVersion = reader.GetInt64(18)
    };

    private Task<SqliteConnection> OpenAsync(CancellationToken token) =>
        CatalogDatabaseAccess.OpenAsync(_connectionString, token);
}
