using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Metadata;

namespace PhotoShelf.Infrastructure.Sqlite;

public sealed record MetadataQueueSummary(long DueCount, long DeferredRetryCount, DateTime? NextRetryAtUtc);
public sealed record MetadataCatalogUpdate(SavedMediaItem Expected, CaptureDateReadResult Result, DateTime AttemptedAtUtc);

public sealed partial class SqliteDesktopCatalogStore
{
    /// <summary>
    /// Background metadata queue, independent of the visible date sort. Keep one fixed
    /// MetadataDueAtUtc for a pass and advance afterPath to the last returned Path, including
    /// skipped/CAS-rejected rows. New or invalidated earlier paths are picked up next pass.
    /// The capped Recent UI view is not a supported background queue scope.
    /// </summary>
    public Task<IReadOnlyList<SavedMediaItem>> QueryMetadataDuePageAsync(CatalogViewQuery scope,
        string? afterPath = null, int pageSize = 128, CancellationToken token = default) =>
        Task.Run<IReadOnlyList<SavedMediaItem>>(async () =>
        {
            if (pageSize is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(pageSize));
            RequireMetadataDue(scope);
            await using var connection = await OpenAsync(token);
            using var interrupt = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
            await using var command = BuildMetadataPageCommand(scope, afterPath, pageSize, connection, token);
            try
            {
                var result = new List<SavedMediaItem>(pageSize);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    token.ThrowIfCancellationRequested();
                    result.Add(ReadItem(reader));
                }
                return result;
            }
            catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        }, token);

    /// <summary>One pending-index aggregate for progress at pass boundaries, never a per-file count.</summary>
    public Task<MetadataQueueSummary> GetMetadataQueueSummaryAsync(CatalogViewQuery scope,
        CancellationToken token = default) => Task.Run(async () =>
    {
        var due = RequireMetadataDue(scope);
        await using var connection = await OpenAsync(token);
        using var interrupt = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        await using var command = BuildMetadataSummaryCommand(scope, due, connection, token);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            await reader.ReadAsync(token);
            return new MetadataQueueSummary(reader.GetInt64(0), reader.GetInt64(1),
                reader.IsDBNull(2) ? null : new DateTime(reader.GetInt64(2), DateTimeKind.Utc));
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }, token);

    private static DateTime RequireMetadataDue(CatalogViewQuery scope)
    {
        // Recent is a capped, moving UI view: its LIMIT 500 before a queue cursor would
        // change membership as results become terminal. Background indexing is not that view.
        if (scope.ViewMode == "Recent")
            throw new ArgumentException("The capped Recent view cannot be used as a metadata queue scope.", nameof(scope));
        return scope.MetadataDueAtUtc?.ToUniversalTime()
            ?? throw new ArgumentException("MetadataDueAtUtc must be fixed for the metadata pass.", nameof(scope));
    }

    private static SqliteCommand BuildMetadataPageCommand(CatalogViewQuery scope, string? afterPath,
        int pageSize, SqliteConnection connection, CancellationToken token)
    {
        var command = connection.CreateCommand();
        try
        {
            var from = BuildFromWithIndex(scope, command, token, "ix_desktop_metadata_queue", preserveIndexSource: true);
            var where = BuildGroupFilter(scope, command);
            if (afterPath is not null)
            {
                where += " AND path_key>$metadataAfter";
                command.Parameters.AddWithValue("$metadataAfter", NormalizePathKey(afterPath));
            }
            command.CommandText = $"SELECT {ItemColumns} FROM {from} WHERE {where} ORDER BY path_key ASC LIMIT $metadataTake;";
            command.Parameters.AddWithValue("$metadataTake", pageSize);
            return command;
        }
        catch { command.Dispose(); throw; }
    }

    private static SqliteCommand BuildMetadataSummaryCommand(CatalogViewQuery scope, DateTime due,
        SqliteConnection connection, CancellationToken token)
    {
        var command = connection.CreateCommand();
        try
        {
            // Keep exactly the queue's eligibility predicates, including indexed=0 after
            // reset even when the previous status/date is retained. Include future retries.
            var allPending = scope with { MetadataDueAtUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) };
            var from = BuildFromWithIndex(allPending, command, token, "ix_desktop_metadata_queue", preserveIndexSource: true);
            command.CommandText = $"""
                SELECT COALESCE(SUM(CASE WHEN metadata_retry_ticks IS NULL OR metadata_retry_ticks<=$queueDue THEN 1 ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN metadata_retry_ticks>$queueDue THEN 1 ELSE 0 END),0),
                    MIN(CASE WHEN metadata_retry_ticks>$queueDue THEN metadata_retry_ticks END)
                FROM {from} WHERE {BuildGroupFilter(scope, command)};
                """;
            command.Parameters.AddWithValue("$queueDue", due.Ticks);
            return command;
        }
        catch { command.Dispose(); throw; }
    }

    /// <summary>
    /// Commit at most 128 previously read outcomes in one short transaction. False entries
    /// failed the catalog snapshot guard. Cancellation or errors before commit roll back
    /// the whole batch; once commit starts, its result is returned without a late cancellation
    /// check so the caller can publish every accepted outcome before draining the worker.
    /// This method never opens, changes, or removes original media files.
    /// </summary>
    public Task<IReadOnlyList<bool>> UpdateMetadataResultsAsync(IReadOnlyList<MetadataCatalogUpdate> updates,
        CancellationToken token = default)
    {
        if (updates.Count > 128) throw new ArgumentOutOfRangeException(nameof(updates), "Metadata batches are limited to 128 outcomes.");
        // SavedMediaItem is mutable; capture all CAS values before scheduling a worker.
        var snapshots = updates.Select(update => new MetadataCatalogUpdate(new SavedMediaItem
        {
            Path = update.Expected.Path, AssetId = update.Expected.AssetId, SizeBytes = update.Expected.SizeBytes,
            FileModifiedAt = update.Expected.FileModifiedAt, ObservationVersion = update.Expected.ObservationVersion,
            MetadataAttemptedAtUtc = update.Expected.MetadataAttemptedAtUtc
        }, update.Result, update.AttemptedAtUtc)).ToArray();
        return Task.Run<IReadOnlyList<bool>>(async () =>
        {
            if (snapshots.Length == 0) return Array.Empty<bool>();
            IReadOnlyList<bool> committed = Array.Empty<bool>();
            await CatalogDatabaseAccess.WriteAsync(_directory, async () =>
            {
                await using var connection = await OpenAsync(token);
                committed = await ExecuteMetadataBatchAsync(connection, snapshots, token);
            }, token);
            return committed;
        }, token);
    }

    private static async Task<IReadOnlyList<bool>> ExecuteMetadataBatchAsync(SqliteConnection connection,
        IReadOnlyList<MetadataCatalogUpdate> updates, CancellationToken token)
    {
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            UPDATE desktop_media_items SET
                capture_date_ticks=CASE WHEN $authoritative=1 THEN $capture ELSE capture_date_ticks END,
                capture_month=CASE WHEN $authoritative=1 THEN $month ELSE capture_month END,
                metadata_indexed=$terminal,metadata_status=$status,metadata_attempted_ticks=$attempted,
                metadata_retry_ticks=$retry,metadata_error_code=$error
            WHERE path_key=$path AND asset_id=$asset AND size_bytes=$size AND file_modified_utc_ticks=$modified
                AND observation_version=$version AND metadata_attempted_ticks IS $expectedAttempted
                AND is_quarantined=0 AND is_video=0
                AND (availability=0 OR (availability=4 AND availability_error_code IS NULL));
            """;
        foreach (var name in new[] { "$authoritative", "$capture", "$month", "$terminal", "$status", "$attempted",
            "$retry", "$error", "$path", "$asset", "$size", "$modified", "$version", "$expectedAttempted" })
            command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        var accepted = new bool[updates.Count];
        for (var index = 0; index < updates.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var update = updates[index]; var expected = update.Expected; var result = update.Result;
            command.Parameters["$authoritative"].Value = result.IsAuthoritative ? 1 : 0;
            command.Parameters["$capture"].Value = (object?)result.CaptureDate?.Ticks ?? DBNull.Value;
            command.Parameters["$month"].Value = (object?)Month(result.CaptureDate) ?? DBNull.Value;
            command.Parameters["$terminal"].Value = result.IsTerminal ? 1 : 0;
            command.Parameters["$status"].Value = (int)result.Status;
            command.Parameters["$attempted"].Value = update.AttemptedAtUtc.ToUniversalTime().Ticks;
            command.Parameters["$retry"].Value = (object?)result.RetryAtUtc(update.AttemptedAtUtc.ToUniversalTime())?.Ticks ?? DBNull.Value;
            command.Parameters["$error"].Value = (object?)result.ErrorCode ?? DBNull.Value;
            command.Parameters["$path"].Value = NormalizePathKey(expected.Path);
            command.Parameters["$asset"].Value = expected.AssetId;
            command.Parameters["$size"].Value = expected.SizeBytes;
            command.Parameters["$modified"].Value = expected.FileModifiedAt?.ToUniversalTime().Ticks ?? 0;
            command.Parameters["$version"].Value = expected.ObservationVersion;
            command.Parameters["$expectedAttempted"].Value = (object?)expected.MetadataAttemptedAtUtc?.ToUniversalTime().Ticks ?? DBNull.Value;
            accepted[index] = await command.ExecuteNonQueryAsync(token) == 1;
        }
        token.ThrowIfCancellationRequested();
        // The short durable commit is the point of no return: finish it and report its
        // outcome even if cancellation races it. Never report rolled-back accepted rows.
        await transaction.CommitAsync(CancellationToken.None);
        return accepted;
    }
}
