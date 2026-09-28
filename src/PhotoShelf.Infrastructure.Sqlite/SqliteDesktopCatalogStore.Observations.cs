using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;

namespace PhotoShelf.Infrastructure.Sqlite;

public sealed partial class SqliteDesktopCatalogStore
{
    public Task<IReadOnlyList<SavedMediaItem>> QuerySubtreePageAsync(string folder, string? afterPath = null,
        int pageSize = 256, CancellationToken token = default, bool includeSubdirectories = true) => Task.Run<IReadOnlyList<SavedMediaItem>>(async () =>
    {
        if (pageSize is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        var scope = includeSubdirectories ? "path_key>=$start AND path_key<$end" : "folder_key=$folder";
        command.CommandText = $"SELECT {ItemColumns} FROM desktop_media_items WHERE is_quarantined=0 AND {scope} AND path_key>$after ORDER BY path_key LIMIT $take;";
        var root = NormalizePathKey(folder);
        if (includeSubdirectories)
        {
            command.Parameters.AddWithValue("$start", root + "/"); command.Parameters.AddWithValue("$end", root + "0");
        }
        else command.Parameters.AddWithValue("$folder", root);
        command.Parameters.AddWithValue("$after", afterPath is null ? "" : NormalizePathKey(afterPath)); command.Parameters.AddWithValue("$take", pageSize);
        var result = new List<SavedMediaItem>(pageSize);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(ReadItem(reader));
        return result;
    }, token);

    public Task<IReadOnlyList<string>> QueryKnownFoldersPageAsync(string? afterFolder = null, int pageSize = 256,
        CancellationToken token = default) => Task.Run<IReadOnlyList<string>>(async () =>
    {
        if (pageSize is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var connection = await OpenAsync(token); await using var command = connection.CreateCommand();
        // Preserve the casing/separators of one stored path; grouping uses the normalized indexed key.
        command.CommandText = "SELECT substr(MIN(path),1,length(folder_key)) FROM desktop_media_items WHERE is_quarantined=0 AND folder_key>$after GROUP BY folder_key ORDER BY folder_key LIMIT $take;";
        command.Parameters.AddWithValue("$after", afterFolder is null ? "" : NormalizePathKey(afterFolder)); command.Parameters.AddWithValue("$take", pageSize);
        var result = new List<string>(pageSize); await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) { var folder = reader.GetString(0); result.Add(folder.Length == 2 && folder[1] == ':' ? folder + "\\" : folder); }
        return result;
    }, token);

    public Task<IReadOnlyList<SavedMediaItem>> FindByFileIdentityAsync(string identity, int limit = 3,
        CancellationToken token = default) => Task.Run<IReadOnlyList<SavedMediaItem>>(async () =>
    {
        if (string.IsNullOrWhiteSpace(identity)) return Array.Empty<SavedMediaItem>();
        if (limit is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(token); await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ItemColumns} FROM desktop_media_items WHERE file_identity=$identity LIMIT $take;";
        command.Parameters.AddWithValue("$identity", identity); command.Parameters.AddWithValue("$take", limit);
        var result = new List<SavedMediaItem>(); await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(ReadItem(reader));
        return result;
    }, token);

    /// <summary>Apply a read-only filesystem result. A supplied snapshot must still match; null permits insert only.</summary>
    public Task<bool> ApplyObservationAsync(FileObservation observation, SavedMediaItem? expected = null,
        CancellationToken token = default) => Task.Run(async () =>
    {
        var changed = false;
        await CatalogDatabaseAccess.WriteAsync(_directory, async () =>
        {
            await using var connection = await OpenAsync(token); await using var transaction = connection.BeginTransaction();
            changed = await ApplyObservationCoreAsync(connection, transaction, observation, expected, token);
            await transaction.CommitAsync(token);
        }, token);
        return changed;
    }, token);

    /// <summary>One bounded transaction per batch. Return values correspond exactly to the submitted snapshots.</summary>
    public Task<IReadOnlyList<bool>> ApplyObservationsAsync(IReadOnlyList<(FileObservation Observation, SavedMediaItem? Expected)> observations,
        CancellationToken token = default) => Task.Run<IReadOnlyList<bool>>(async () =>
    {
        if (observations.Count > 256) throw new ArgumentOutOfRangeException(nameof(observations));
        var results = new List<bool>(observations.Count);
        await CatalogDatabaseAccess.WriteAsync(_directory, async () =>
        {
            await using var connection = await OpenAsync(token); await using var transaction = connection.BeginTransaction();
            foreach (var (observation, expected) in observations)
                results.Add(await ApplyObservationCoreAsync(connection, transaction, observation, expected, token));
            await transaction.CommitAsync(token);
        }, token);
        return results;
    }, token);

    public Task<IReadOnlyDictionary<string, SavedMediaItem>> GetItemsByPathsAsync(IReadOnlyList<string> paths,
        CancellationToken token = default) => Task.Run<IReadOnlyDictionary<string, SavedMediaItem>>(async () =>
    {
        if (paths.Count > 256) throw new ArgumentOutOfRangeException(nameof(paths));
        var results = new Dictionary<string, SavedMediaItem>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0) return results;
        await using var connection = await OpenAsync(token); await using var command = connection.CreateCommand();
        var names = new List<string>(paths.Count);
        for (var i = 0; i < paths.Count; i++) { var name = "$p" + i; names.Add(name); command.Parameters.AddWithValue(name, NormalizePathKey(paths[i])); }
        command.CommandText = $"SELECT {ItemColumns} FROM desktop_media_items WHERE path_key IN ({string.Join(',', names)});";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) { var item = ReadItem(reader); results[item.Path] = item; }
        return results;
    }, token);

    private static async Task<bool> ApplyObservationCoreAsync(SqliteConnection connection, SqliteTransaction transaction,
        FileObservation observation, SavedMediaItem? expected, CancellationToken token)
    {
        var item = observation.Item;
        if (string.IsNullOrWhiteSpace(item.Path) || item.AvailabilityCheckedAtUtc is null) throw new ArgumentException("A checked path is required.", nameof(observation));
        var changed = false;
        if (expected is null)
        {
            if (item.Availability != FileAvailability.Available) return false;
            await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO desktop_media_items(asset_id,path,path_key,folder_key,search_key,size_bytes,file_modified_utc_ticks,
                    file_local_ticks,file_month,is_video,is_hidden_or_system,last_seen_utc,availability,availability_checked_ticks,file_identity)
                VALUES($id,$path,$key,$folder,$key,$size,$modified,$local,$month,$video,$hidden,
                    strftime('%Y-%m-%dT%H:%M:%fZ','now'),0,$checked,$identity) ON CONFLICT(path_key) DO NOTHING;
                """;
            AddObservedParameters(insert, item);
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            changed = await insert.ExecuteNonQueryAsync(token) == 1;
            if (changed) await InvalidateCachesAsync(connection, transaction, item.Path, token);
        }
        else
        {
            if (NormalizePathKey(item.Path) != NormalizePathKey(expected.Path)) throw new ArgumentException("Use rename reconciliation for a changed path.", nameof(observation));
            if (expected.AvailabilityCheckedAtUtc is { } previousCheck && item.AvailabilityCheckedAtUtc < previousCheck) return false;
            if (!await MatchesSnapshotAsync(connection, transaction, expected, token)) return false;
            var available = item.Availability == FileAvailability.Available;
            var fingerprintChanged = available && (item.SizeBytes != expected.SizeBytes || UtcTicks(item.FileModifiedAt) != UtcTicks(expected.FileModifiedAt)
                || (expected.FileIdentity is not null && item.FileIdentity is not null && expected.FileIdentity != item.FileIdentity));
            var invalidate = available && (fingerprintChanged || observation.ForceContentRevalidation);
            if (invalidate)
            {
                await InvalidateCachesAsync(connection, transaction, expected.Path, token);
                if (expected.Path != item.Path) await InvalidateCachesAsync(connection, transaction, item.Path, token);
            }
            else if (available && expected.Path != item.Path)
                await RelocateCachesAsync(connection, transaction, expected.Path, item.Path, token);
            await using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = """
                UPDATE desktop_media_items SET availability=$availability,availability_checked_ticks=$checked,availability_error_code=$error,
                    path=CASE WHEN $available=1 THEN $path ELSE path END,
                    observation_version=observation_version+1,
                    file_identity=CASE WHEN $available=1 THEN $identity ELSE file_identity END,
                    size_bytes=CASE WHEN $available=1 THEN $size ELSE size_bytes END,
                    file_modified_utc_ticks=CASE WHEN $available=1 THEN $modified ELSE file_modified_utc_ticks END,
                    file_local_ticks=CASE WHEN $available=1 THEN $local ELSE file_local_ticks END,
                    file_month=CASE WHEN $available=1 THEN $month ELSE file_month END,
                    is_video=CASE WHEN $available=1 THEN $video ELSE is_video END,
                    is_hidden_or_system=CASE WHEN $available=1 THEN $hidden ELSE is_hidden_or_system END,
                    capture_date_ticks=CASE WHEN $changed=1 THEN NULL ELSE capture_date_ticks END,
                    capture_month=CASE WHEN $changed=1 THEN NULL ELSE capture_month END,
                    metadata_indexed=CASE WHEN $invalidate=1 THEN 0 ELSE metadata_indexed END,
                    metadata_status=CASE WHEN $invalidate=1 THEN 0 ELSE metadata_status END,
                    metadata_attempted_ticks=CASE WHEN $invalidate=1 THEN NULL ELSE metadata_attempted_ticks END,
                    metadata_retry_ticks=CASE WHEN $invalidate=1 THEN NULL ELSE metadata_retry_ticks END,
                    metadata_error_code=CASE WHEN $invalidate=1 THEN NULL ELSE metadata_error_code END,
                    last_seen_utc=CASE WHEN $available=1 THEN strftime('%Y-%m-%dT%H:%M:%fZ','now') ELSE last_seen_utc END
                WHERE asset_id=$id AND is_quarantined=0;
                """;
            AddObservedParameters(update, item);
            update.Parameters.AddWithValue("$id", expected.AssetId);
            update.Parameters.AddWithValue("$available", available ? 1 : 0);
            update.Parameters.AddWithValue("$changed", fingerprintChanged ? 1 : 0);
            update.Parameters.AddWithValue("$invalidate", invalidate ? 1 : 0);
            changed = await update.ExecuteNonQueryAsync(token) == 1;
        }
        return changed;
    }

    public Task<bool> SetAvailabilityAsync(SavedMediaItem expected, FileAvailability availability, DateTime checkedUtc,
        string? errorCode = null, CancellationToken token = default)
    {
        if (availability == FileAvailability.Available) throw new ArgumentException("An available result requires a full file observation.", nameof(availability));
        return ApplyObservationAsync(new(new SavedMediaItem { Path = expected.Path, Availability = availability,
            AvailabilityCheckedAtUtc = checkedUtc, AvailabilityErrorCode = errorCode }), expected, token);
    }

    /// <summary>Bounded unavailable updates; never infer availability from a root alone or overwrite a newer observation.</summary>
    public async Task<int> SetSubtreeAvailabilityAsync(string root, FileAvailability availability, DateTime checkedUtc,
        string? errorCode = null, CancellationToken token = default)
    {
        if (availability == FileAvailability.Available) throw new ArgumentException("Probe individual files before declaring them available.", nameof(availability));
        var total = 0; string? after = null;
        while (true)
        {
            var page = await QuerySubtreePageAsync(root, after, token: token).ConfigureAwait(false);
            if (page.Count == 0) return total;
            await Task.Run(() => CatalogDatabaseAccess.WriteAsync(_directory, async () =>
            {
                await using var connection = await OpenAsync(token); await using var transaction = connection.BeginTransaction();
                await using var update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = """
                    UPDATE desktop_media_items SET availability=$availability,availability_checked_ticks=$checked,
                        availability_error_code=$error,observation_version=observation_version+1
                    WHERE asset_id=$id AND path_key=$path AND observation_version=$version AND is_quarantined=0
                        AND (availability_checked_ticks IS NULL OR availability_checked_ticks<=$checked);
                    """;
                foreach (var item in page)
                {
                    update.Parameters.Clear(); update.Parameters.AddWithValue("$availability", (int)availability);
                    update.Parameters.AddWithValue("$checked", checkedUtc.ToUniversalTime().Ticks);
                    update.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
                    update.Parameters.AddWithValue("$id", item.AssetId); update.Parameters.AddWithValue("$path", NormalizePathKey(item.Path));
                    update.Parameters.AddWithValue("$version", item.ObservationVersion);
                    total += await update.ExecuteNonQueryAsync(token);
                }
                await transaction.CommitAsync(token);
            }, token), token).ConfigureAwait(false);
            after = page[^1].Path;
        }
    }

    /// <summary>Caller proves source absent and destination unchanged using worker probes. Identity alone never merges records.</summary>
    public Task<bool> TryReconcileExternalRenameAsync(SavedMediaItem expectedSource, SavedMediaItem observedDestination,
        CancellationToken token = default) => Task.Run(async () =>
    {
        if (observedDestination.Availability != FileAvailability.Available || observedDestination.AvailabilityCheckedAtUtc is null
            || (expectedSource.AvailabilityCheckedAtUtc is { } previousCheck && observedDestination.AvailabilityCheckedAtUtc < previousCheck)
            || string.IsNullOrWhiteSpace(expectedSource.FileIdentity) || expectedSource.FileIdentity != observedDestination.FileIdentity
            || expectedSource.SizeBytes != observedDestination.SizeBytes || UtcTicks(expectedSource.FileModifiedAt) != UtcTicks(observedDestination.FileModifiedAt)) return false;
        var moved = false;
        await CatalogDatabaseAccess.WriteAsync(_directory, async () =>
        {
            await using var connection = await OpenAsync(token); await using var transaction = connection.BeginTransaction();
            if (!await MatchesSnapshotAsync(connection, transaction, expectedSource, token)) return;
            await using var check = connection.CreateCommand(); check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM desktop_media_items WHERE file_identity=$identity;";
            check.Parameters.AddWithValue("$identity", expectedSource.FileIdentity);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(token)) != 1) return;
            check.CommandText = "SELECT COUNT(*) FROM desktop_media_items WHERE path_key=$destination AND asset_id<>$id;";
            check.Parameters.AddWithValue("$destination", NormalizePathKey(observedDestination.Path)); check.Parameters.AddWithValue("$id", expectedSource.AssetId);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(token)) != 0) return;
            // Cache rows are derived. A stale destination cache may be discarded, but no catalog identity is replaced.
            await RelocateCachesAsync(connection, transaction, expectedSource.Path, observedDestination.Path, token);
            await using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = """
                UPDATE desktop_media_items SET path=$path,path_key=$key,folder_key=$folder,search_key=$key,
                    availability=0,availability_checked_ticks=$checked,availability_error_code=NULL,
                    is_hidden_or_system=$hidden,observation_version=observation_version+1,
                    last_seen_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE asset_id=$id;
                """;
            AddObservedParameters(update, observedDestination); update.Parameters.AddWithValue("$id", expectedSource.AssetId);
            moved = await update.ExecuteNonQueryAsync(token) == 1;
            await transaction.CommitAsync(token);
        }, token);
        return moved;
    }, token);

    private static async Task<bool> MatchesSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction,
        SavedMediaItem expected, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM desktop_media_items WHERE asset_id=$id AND path_key=$key AND size_bytes=$size AND file_modified_utc_ticks=$modified AND observation_version=$version AND is_quarantined=0);";
        command.Parameters.AddWithValue("$id", expected.AssetId); command.Parameters.AddWithValue("$key", NormalizePathKey(expected.Path));
        command.Parameters.AddWithValue("$size", expected.SizeBytes); command.Parameters.AddWithValue("$modified", UtcTicks(expected.FileModifiedAt));
        command.Parameters.AddWithValue("$version", expected.ObservationVersion);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 1;
    }

    private static void AddObservedParameters(SqliteCommand command, SavedMediaItem item)
    {
        var local = item.FileModifiedAt is { Kind: DateTimeKind.Utc } utc ? utc.ToLocalTime() : item.FileModifiedAt;
        command.Parameters.AddWithValue("$path", item.Path); command.Parameters.AddWithValue("$key", NormalizePathKey(item.Path));
        command.Parameters.AddWithValue("$folder", FolderKey(item.Path)); command.Parameters.AddWithValue("$size", item.SizeBytes);
        command.Parameters.AddWithValue("$modified", UtcTicks(item.FileModifiedAt)); command.Parameters.AddWithValue("$local", local?.Ticks ?? 0);
        command.Parameters.AddWithValue("$month", (object?)Month(local) ?? DBNull.Value); command.Parameters.AddWithValue("$video", item.IsVideo ? 1 : 0);
        command.Parameters.AddWithValue("$hidden", item.IsHiddenOrSystem ? 1 : 0); command.Parameters.AddWithValue("$availability", (int)item.Availability);
        command.Parameters.AddWithValue("$checked", (object?)item.AvailabilityCheckedAtUtc?.ToUniversalTime().Ticks ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)item.AvailabilityErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$identity", (object?)item.FileIdentity ?? DBNull.Value);
    }

    private static long UtcTicks(DateTime? time) => time?.ToUniversalTime().Ticks ?? 0;
    private static async Task RelocateCachesAsync(SqliteConnection connection, SqliteTransaction transaction,
        string source, string destination, CancellationToken token)
    {
        if (string.Equals(source, destination, StringComparison.Ordinal)) return;
        await InvalidateCachesAsync(connection, transaction, destination, token);
        foreach (var table in new[] { "desktop_metadata_cache", "duplicate_hash_cache" })
        {
            if (!await TableExistsAsync(connection, transaction, table, token)) continue;
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"UPDATE {table} SET path=$destination WHERE path=$source;";
            command.Parameters.AddWithValue("$source", source); command.Parameters.AddWithValue("$destination", destination);
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task InvalidateCachesAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken token)
    {
        foreach (var table in new[] { "desktop_metadata_cache", "duplicate_hash_cache" })
        {
            if (!await TableExistsAsync(connection, transaction, table, token)) continue;
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE path=$path;";
            command.Parameters.AddWithValue("$path", path); await command.ExecuteNonQueryAsync(token);
        }
    }
}
