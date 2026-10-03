using System.Numerics;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Duplicates;

namespace PhotoShelf.Infrastructure.Sqlite;

public sealed record SimilarPhotoMatch(
    SavedMediaItem Item,
    PerceptualFingerprint Fingerprint,
    int FolderMask = 0);

public sealed record SimilarPhotoMember(
    SavedMediaItem Item,
    int DifferenceDistance,
    int AverageDistance,
    bool IsReference);

public sealed record SimilarPhotoGroupPage(
    long Id,
    long TotalFiles,
    string ReferencePath,
    int MaximumDifferenceDistance,
    int MaximumAverageDistance,
    long MemberOffset,
    IReadOnlyList<SimilarPhotoMember> Items);

/// <summary>
/// Owned, disk-backed review snapshot for visual similarity. Groups are deliberately greedy:
/// every member is verified against one stable reference, so similarity never spreads through
/// an unbounded A≈B≈C chain. It is a review aid, not duplicate proof.
/// </summary>
public sealed class SimilarPhotoSearchSession : IAsyncDisposable
{
    public const int GroupsPerPage = 16;
    public const int MembersPerPage = 48;
    public const int MaximumDifferenceDistance = 3;
    public const int MaximumAverageDistance = 12;

    private readonly string _directory;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _completed;
    private bool _disposed;

    private SimilarPhotoSearchSession(string directory, SqliteConnection connection)
    {
        _directory = directory;
        _connection = connection;
    }

    public long GroupCount { get; private set; }
    public long IndexedItemCount { get; private set; }

    public static Task<SimilarPhotoSearchSession> CreateAsync(string catalogDirectory,
        CancellationToken token = default) => Task.Run(async () =>
    {
        var parent = Path.Combine(catalogDirectory, "similar-photo-searches");
        Directory.CreateDirectory(parent);
        var directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.sqlite");
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        var session = new SimilarPhotoSearchSession(directory, connection);
        try
        {
            await connection.OpenAsync(token);
            connection.CreateCollation("PHOTOSHELF_PATH",
                (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
            connection.CreateFunction<long, long, int>("ps_hamming", (left, right) =>
                BitOperations.PopCount(unchecked((ulong)left) ^ unchecked((ulong)right)));
            await session.RunAsync(async () =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA cache_size=-8192;
                    PRAGMA temp_store=FILE;
                    PRAGMA journal_mode=DELETE;
                    CREATE TABLE items(
                        path TEXT PRIMARY KEY COLLATE PHOTOSHELF_PATH,
                        asset_id TEXT NOT NULL,
                        size INTEGER NOT NULL,
                        modified INTEGER,
                        captured INTEGER,
                        difference_hash INTEGER NOT NULL,
                        average_hash INTEGER NOT NULL,
                        width INTEGER NOT NULL,
                        height INTEGER NOT NULL,
                        folder_mask INTEGER NOT NULL,
                        group_id INTEGER NOT NULL,
                        difference_distance INTEGER NOT NULL,
                        average_distance INTEGER NOT NULL);
                    CREATE INDEX ix_similar_items_group ON items(group_id,path);
                    CREATE TABLE groups(
                        id INTEGER PRIMARY KEY,
                        reference_path TEXT NOT NULL COLLATE PHOTOSHELF_PATH,
                        total INTEGER NOT NULL,
                        maximum_difference INTEGER NOT NULL,
                        maximum_average INTEGER NOT NULL,
                        folder_mask INTEGER NOT NULL);
                    CREATE TABLE representative_bands(
                        group_id INTEGER NOT NULL,
                        band_index INTEGER NOT NULL,
                        band_value INTEGER NOT NULL,
                        PRIMARY KEY(group_id,band_index));
                    CREATE INDEX ix_similar_representative_band
                        ON representative_bands(band_index,band_value,group_id);
                    """;
                await command.ExecuteNonQueryAsync(token);
                return 0;
            }, token);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }, token);

    public Task AddAsync(IReadOnlyList<SimilarPhotoMatch> batch, CancellationToken token = default) =>
        RunAsync(async () =>
        {
            if (_completed) throw new InvalidOperationException("Search snapshot has already completed.");
            if (batch.Count > 128)
                throw new ArgumentOutOfRangeException(nameof(batch), "Similarity writes are limited to 128 candidates.");
            await using var transaction = _connection.BeginTransaction();
            foreach (var match in batch)
            {
                token.ThrowIfCancellationRequested();
                if (match.Fingerprint.AlgorithmVersion != PerceptualFingerprint.CurrentAlgorithmVersion)
                    throw new ArgumentException("Only the current fingerprint algorithm is supported.", nameof(batch));
                if (await ContainsPathAsync(match.Item.Path, transaction, token)) continue;
                var candidate = await FindRepresentativeAsync(match.Fingerprint, transaction, token);
                if (candidate is null)
                    await CreateGroupAsync(match, transaction, token);
                else
                    await AddToGroupAsync(match, candidate.Value, transaction, token);
                IndexedItemCount++;
            }
            await transaction.CommitAsync(token);
            return 0;
        }, token);

    public Task CompleteAsync(bool compareFolders, CancellationToken token = default) => RunAsync(async () =>
    {
        if (_completed) return 0;
        await using var transaction = _connection.BeginTransaction();
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM representative_bands WHERE group_id IN
                (SELECT id FROM groups WHERE total<2 OR ($compare=1 AND (folder_mask & 3)<>3));
            DELETE FROM items WHERE group_id IN
                (SELECT id FROM groups WHERE total<2 OR ($compare=1 AND (folder_mask & 3)<>3));
            DELETE FROM groups WHERE total<2 OR ($compare=1 AND (folder_mask & 3)<>3);
            """;
        command.Parameters.AddWithValue("$compare", compareFolders ? 1 : 0);
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT count(*) FROM groups;";
        GroupCount = Convert.ToInt64(await command.ExecuteScalarAsync(token));
        await transaction.CommitAsync(token);
        _completed = true;
        return 0;
    }, token);

    public Task<IReadOnlyList<SimilarPhotoGroupPage>> ReadGroupsAsync(long offset,
        CancellationToken token = default) => RunAsync<IReadOnlyList<SimilarPhotoGroupPage>>(async () =>
    {
        RequireComplete();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var ids = new List<long>(GroupsPerPage);
        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM groups ORDER BY total DESC,id LIMIT $limit OFFSET $offset;";
            command.Parameters.AddWithValue("$limit", GroupsPerPage);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) ids.Add(reader.GetInt64(0));
        }
        var result = new List<SimilarPhotoGroupPage>(ids.Count);
        foreach (var id in ids) result.Add(await ReadGroupCoreAsync(id, 0, token));
        return result;
    }, token);

    public Task<SimilarPhotoGroupPage> ReadGroupAsync(long id, long memberOffset,
        CancellationToken token = default) => RunAsync(() => ReadGroupCoreAsync(id, memberOffset, token), token);

    private async Task<SimilarPhotoGroupPage> ReadGroupCoreAsync(long id, long memberOffset,
        CancellationToken token)
    {
        RequireComplete();
        if (memberOffset < 0) throw new ArgumentOutOfRangeException(nameof(memberOffset));
        long total;
        string reference;
        int maximumDifference, maximumAverage;
        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT reference_path,total,maximum_difference,maximum_average FROM groups WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new KeyNotFoundException("Similarity group does not exist.");
            reference = reader.GetString(0);
            total = reader.GetInt64(1);
            maximumDifference = reader.GetInt32(2);
            maximumAverage = reader.GetInt32(3);
        }

        var items = new List<SimilarPhotoMember>(MembersPerPage + 1);
        await using var itemsCommand = _connection.CreateCommand();
        itemsCommand.Parameters.AddWithValue("$id", id);
        itemsCommand.Parameters.AddWithValue("$reference", reference);
        itemsCommand.CommandText = """
            SELECT path,size,modified,captured,difference_distance,average_distance
            FROM items WHERE group_id=$id AND path=$reference;
            """;
        await using (var reader = await itemsCommand.ExecuteReaderAsync(token))
            if (await reader.ReadAsync(token)) items.Add(ReadMember(reader, true));
        if (items.Count != 1) throw new IOException("Reference image is unavailable in the similarity snapshot.");

        itemsCommand.Parameters.AddWithValue("$offset", memberOffset);
        itemsCommand.Parameters.AddWithValue("$limit", MembersPerPage);
        itemsCommand.CommandText = """
            SELECT path,size,modified,captured,difference_distance,average_distance
            FROM items WHERE group_id=$id AND path<>$reference
            ORDER BY difference_distance,average_distance,path LIMIT $limit OFFSET $offset;
            """;
        await using (var reader = await itemsCommand.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) items.Add(ReadMember(reader, false));
        return new(id, total, reference, maximumDifference, maximumAverage, memberOffset, items);
    }

    private async Task<bool> ContainsPathAsync(string path, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM items WHERE path=$path);";
        command.Parameters.AddWithValue("$path", path);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 0;
    }

    private async Task<(long GroupId, int Difference, int Average)?> FindRepresentativeAsync(
        PerceptualFingerprint fingerprint, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        var bands = Enumerable.Range(0, PerceptualFingerprint.BandCount)
            .Select(index => $"(b.band_index={index} AND b.band_value=$band{index})");
        command.CommandText = $"""
            SELECT DISTINCT g.id,
                ps_hamming(r.difference_hash,$difference) AS difference_distance,
                ps_hamming(r.average_hash,$average) AS average_distance
            FROM representative_bands AS b
            JOIN groups AS g ON g.id=b.group_id
            JOIN items AS r ON r.path=g.reference_path
            WHERE ({string.Join(" OR ", bands)})
                AND ps_hamming(r.difference_hash,$difference)<=$maximumDifference
                AND ps_hamming(r.average_hash,$average)<=$maximumAverage
                AND abs(r.width*$height-$width*r.height)*20
                    <= max(r.width*$height,$width*r.height)
            ORDER BY difference_distance,average_distance,g.id LIMIT 1;
            """;
        for (var index = 0; index < PerceptualFingerprint.BandCount; index++)
            command.Parameters.AddWithValue($"$band{index}", fingerprint.DifferenceBand(index));
        command.Parameters.AddWithValue("$difference", unchecked((long)fingerprint.DifferenceHash));
        command.Parameters.AddWithValue("$average", unchecked((long)fingerprint.AverageHash));
        command.Parameters.AddWithValue("$width", fingerprint.PixelWidth);
        command.Parameters.AddWithValue("$height", fingerprint.PixelHeight);
        command.Parameters.AddWithValue("$maximumDifference", MaximumDifferenceDistance);
        command.Parameters.AddWithValue("$maximumAverage", MaximumAverageDistance);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? (reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2))
            : null;
    }

    private async Task CreateGroupAsync(SimilarPhotoMatch match, SqliteTransaction transaction,
        CancellationToken token)
    {
        await using var group = _connection.CreateCommand();
        group.Transaction = transaction;
        group.CommandText = """
            INSERT INTO groups(reference_path,total,maximum_difference,maximum_average,folder_mask)
            VALUES($path,1,0,0,$mask) RETURNING id;
            """;
        group.Parameters.AddWithValue("$path", match.Item.Path);
        group.Parameters.AddWithValue("$mask", match.FolderMask);
        var groupId = Convert.ToInt64(await group.ExecuteScalarAsync(token));
        await InsertItemAsync(match, groupId, 0, 0, transaction, token);

        await using var band = _connection.CreateCommand();
        band.Transaction = transaction;
        band.CommandText = "INSERT INTO representative_bands(group_id,band_index,band_value) VALUES($group,$index,$value);";
        band.Parameters.AddWithValue("$group", groupId);
        var indexParameter = band.Parameters.Add("$index", SqliteType.Integer);
        var valueParameter = band.Parameters.Add("$value", SqliteType.Integer);
        for (var index = 0; index < PerceptualFingerprint.BandCount; index++)
        {
            indexParameter.Value = index;
            valueParameter.Value = match.Fingerprint.DifferenceBand(index);
            await band.ExecuteNonQueryAsync(token);
        }
    }

    private async Task AddToGroupAsync(SimilarPhotoMatch match,
        (long GroupId, int Difference, int Average) candidate, SqliteTransaction transaction,
        CancellationToken token)
    {
        await InsertItemAsync(match, candidate.GroupId, candidate.Difference, candidate.Average, transaction, token);
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE groups SET total=total+1,
                maximum_difference=max(maximum_difference,$difference),
                maximum_average=max(maximum_average,$average),
                folder_mask=folder_mask | $mask WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$difference", candidate.Difference);
        command.Parameters.AddWithValue("$average", candidate.Average);
        command.Parameters.AddWithValue("$mask", match.FolderMask);
        command.Parameters.AddWithValue("$id", candidate.GroupId);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task InsertItemAsync(SimilarPhotoMatch match, long groupId, int difference,
        int average, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO items(path,asset_id,size,modified,captured,difference_hash,average_hash,width,height,
                folder_mask,group_id,difference_distance,average_distance)
            VALUES($path,$asset,$size,$modified,$captured,$difference,$average,$width,$height,$mask,$group,$dd,$ad);
            """;
        command.Parameters.AddWithValue("$path", match.Item.Path);
        command.Parameters.AddWithValue("$asset", match.Item.AssetId);
        command.Parameters.AddWithValue("$size", match.Item.SizeBytes);
        command.Parameters.AddWithValue("$modified", (object?)match.Item.FileModifiedAt?.Ticks ?? DBNull.Value);
        command.Parameters.AddWithValue("$captured", (object?)match.Item.CaptureDate?.Ticks ?? DBNull.Value);
        command.Parameters.AddWithValue("$difference", unchecked((long)match.Fingerprint.DifferenceHash));
        command.Parameters.AddWithValue("$average", unchecked((long)match.Fingerprint.AverageHash));
        command.Parameters.AddWithValue("$width", match.Fingerprint.PixelWidth);
        command.Parameters.AddWithValue("$height", match.Fingerprint.PixelHeight);
        command.Parameters.AddWithValue("$mask", match.FolderMask);
        command.Parameters.AddWithValue("$group", groupId);
        command.Parameters.AddWithValue("$dd", difference);
        command.Parameters.AddWithValue("$ad", average);
        await command.ExecuteNonQueryAsync(token);
    }

    private static SimilarPhotoMember ReadMember(SqliteDataReader reader, bool reference)
    {
        var item = new SavedMediaItem
        {
            Path = reader.GetString(0),
            SizeBytes = reader.GetInt64(1),
            FileModifiedAt = reader.IsDBNull(2) ? null : new DateTime(reader.GetInt64(2), DateTimeKind.Local),
            CaptureDate = reader.IsDBNull(3) ? null : new DateTime(reader.GetInt64(3), DateTimeKind.Unspecified),
            MetadataIndexed = true
        };
        return new(item, reader.GetInt32(4), reader.GetInt32(5), reference);
    }

    private void RequireComplete()
    {
        if (!_completed) throw new InvalidOperationException("Search snapshot is incomplete.");
    }

    private Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken token) => Task.Run(async () =>
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var interrupt = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(_connection.Handle));
            try
            {
                token.ThrowIfCancellationRequested();
                return await action();
            }
            catch (SqliteException) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
        }
        finally
        {
            _gate.Release();
        }
    }, token);

    public ValueTask DisposeAsync() => new(Task.Run(async () =>
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _connection.DisposeAsync();
            foreach (var name in new[] { "session.sqlite", "session.sqlite-journal", "session.sqlite-wal", "session.sqlite-shm" })
                try { File.Delete(Path.Combine(_directory, name)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            try { Directory.Delete(_directory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        finally
        {
            _gate.Release();
        }
    }));
}
