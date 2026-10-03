using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;

namespace PhotoShelf.Infrastructure.Sqlite;

public sealed record DuplicateSearchMatch(SavedMediaItem Item, string Hash, int FolderMask = 0);
public enum DuplicateKeeperRule { NewestFile, OldestFile, ShortestPath, PriorityFolder }
public sealed record DuplicateSelectionSummary(long SelectedFiles, long SelectedGroups, long SelectedBytes);
public sealed record DuplicateQuarantineCandidate(long GroupId, string KeeperPath, string Hash, SavedMediaItem Item);
public sealed record DuplicateGroupPage(long Id, long SizeBytes, string Hash, long TotalFiles, string KeeperPath,
    long MemberOffset, IReadOnlyList<SavedMediaItem> Items, IReadOnlySet<string> SelectedPaths);

/// <summary>Owned disk-backed search result. Candidates and groups never become one managed collection.</summary>
public sealed class DuplicateSearchSession : IAsyncDisposable
{
    public const int GroupsPerPage = 16;
    public const int MembersPerPage = 64;
    private readonly string _directory;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _completed;
    private bool _disposed;
    public long GroupCount { get; private set; }
    private DuplicateSearchSession(string directory, SqliteConnection connection) { _directory = directory; _connection = connection; }

    public static Task<DuplicateSearchSession> CreateAsync(string catalogDirectory, CancellationToken token = default) => Task.Run(async () =>
    {
        var parent = Path.Combine(catalogDirectory, "duplicate-searches"); Directory.CreateDirectory(parent);
        var directory = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.sqlite");
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        var session = new DuplicateSearchSession(directory, connection);
        try
        {
            await connection.OpenAsync(token);
            connection.CreateCollation("PHOTOSHELF_PATH", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
            await session.RunAsync(async () =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA cache_size=-8192; PRAGMA temp_store=FILE; PRAGMA journal_mode=DELETE;
                    CREATE TABLE items(path TEXT PRIMARY KEY COLLATE PHOTOSHELF_PATH, size INTEGER NOT NULL, modified INTEGER, captured INTEGER,
                        hash TEXT NOT NULL, folder_mask INTEGER NOT NULL, removed INTEGER NOT NULL DEFAULT 0,
                        selected INTEGER NOT NULL DEFAULT 0);
                    CREATE INDEX ix_items_hash ON items(size,hash,removed,path);
                    CREATE INDEX ix_items_selected ON items(selected,removed,size,hash,path);
                    CREATE TABLE groups(id INTEGER PRIMARY KEY, size INTEGER NOT NULL, hash TEXT NOT NULL,
                        total INTEGER NOT NULL, keeper TEXT NOT NULL COLLATE PHOTOSHELF_PATH);
                    CREATE INDEX ix_groups_keeper ON groups(keeper);
                    CREATE UNIQUE INDEX ix_groups_hash ON groups(size,hash);
                    """;
                await command.ExecuteNonQueryAsync(token); return 0;
            }, token);
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }, token);

    public Task AddAsync(IReadOnlyList<DuplicateSearchMatch> batch, CancellationToken token = default) => RunAsync(async () =>
    {
        if (_completed) throw new InvalidOperationException("Search snapshot has already completed.");
        if (batch.Count > 128) throw new ArgumentOutOfRangeException(nameof(batch), "Search writes are limited to 128 candidates.");
        await using var transaction = _connection.BeginTransaction();
        await using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO items(path,size,modified,captured,hash,folder_mask) VALUES($path,$size,$modified,$captured,$hash,$mask) ON CONFLICT(path) DO NOTHING;";
        foreach (var match in batch)
        {
            token.ThrowIfCancellationRequested(); var item = match.Item;
            if (match.Hash.Length != 64 || !match.Hash.All(Uri.IsHexDigit)) throw new ArgumentException("A verified SHA-256 is required.");
            command.Parameters.Clear(); command.Parameters.AddWithValue("$path", item.Path); command.Parameters.AddWithValue("$size", item.SizeBytes);
            command.Parameters.AddWithValue("$modified", (object?)item.FileModifiedAt?.Ticks ?? DBNull.Value);
            command.Parameters.AddWithValue("$captured", (object?)item.CaptureDate?.Ticks ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", match.Hash.ToUpperInvariant()); command.Parameters.AddWithValue("$mask", match.FolderMask);
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token); return 0;
    }, token);

    public Task CompleteAsync(bool compareFolders, CancellationToken token = default) => RunAsync(async () =>
    {
        if (_completed) return 0;
        await using var transaction = _connection.BeginTransaction();
        await using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO groups(size,hash,total,keeper)
            SELECT size,hash,count(*),min(path) FROM items GROUP BY size,hash
            HAVING count(*)>1 AND ($compare=0 OR (max(folder_mask & 1)>0 AND max(folder_mask & 2)>0))
            ORDER BY size DESC,hash;
            """;
        command.Parameters.AddWithValue("$compare", compareFolders ? 1 : 0);
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT count(*) FROM groups;";
        GroupCount = Convert.ToInt64(await command.ExecuteScalarAsync(token));
        await transaction.CommitAsync(token); _completed = true; return 0;
    }, token);

    public Task<IReadOnlyList<DuplicateGroupPage>> ReadGroupsAsync(long offset, CancellationToken token = default) => RunAsync<IReadOnlyList<DuplicateGroupPage>>(async () =>
    {
        RequireComplete(); if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var ids = new List<long>(GroupsPerPage);
        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM groups ORDER BY id LIMIT $limit OFFSET $offset;";
            command.Parameters.AddWithValue("$limit", GroupsPerPage); command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) ids.Add(reader.GetInt64(0));
        }
        var groups = new List<DuplicateGroupPage>(ids.Count);
        foreach (var id in ids) groups.Add(await ReadGroupCoreAsync(id, 0, token));
        return groups;
    }, token);

    public Task<DuplicateGroupPage> ReadGroupAsync(long id, long memberOffset, CancellationToken token = default) =>
        RunAsync(() => ReadGroupCoreAsync(id, memberOffset, token), token);

    private async Task<DuplicateGroupPage> ReadGroupCoreAsync(long id, long memberOffset, CancellationToken token)
    {
        RequireComplete(); if (memberOffset < 0) throw new ArgumentOutOfRangeException(nameof(memberOffset));
        long size, count; string hash, keeper;
        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT size,hash,keeper,total FROM groups WHERE id=$id;"; command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new KeyNotFoundException("Duplicate group does not exist.");
            size = reader.GetInt64(0); hash = reader.GetString(1); keeper = reader.GetString(2); count = reader.GetInt64(3);
        }
        await using var itemsCommand = _connection.CreateCommand();
        itemsCommand.Parameters.AddWithValue("$size", size); itemsCommand.Parameters.AddWithValue("$hash", hash);
        itemsCommand.Parameters.AddWithValue("$keeper", keeper);
        itemsCommand.CommandText = "SELECT path,size,modified,captured,selected FROM items WHERE path=$keeper AND removed=0;";
        var items = new List<SavedMediaItem>(MembersPerPage + 1);
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await itemsCommand.ExecuteReaderAsync(token))
            if (await reader.ReadAsync(token))
            {
                items.Add(ReadItem(reader));
                if (reader.GetInt64(4) != 0) selectedPaths.Add(reader.GetString(0));
            }
        if (items.Count != 1) throw new IOException("Сохраняемый экземпляр недоступен в снимке; повторите поиск дублей.");
        itemsCommand.Parameters.AddWithValue("$offset", memberOffset); itemsCommand.Parameters.AddWithValue("$limit", MembersPerPage);
        itemsCommand.CommandText = "SELECT path,size,modified,captured,selected FROM items WHERE size=$size AND hash=$hash AND removed=0 AND path<>$keeper ORDER BY path LIMIT $limit OFFSET $offset;";
        await using (var reader = await itemsCommand.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                items.Add(ReadItem(reader));
                if (reader.GetInt64(4) != 0) selectedPaths.Add(reader.GetString(0));
            }
        return new(id, size, hash, count, keeper, memberOffset, items, selectedPaths);
    }

    public Task SetKeeperAsync(long id, string path, CancellationToken token = default) => RunAsync(async () =>
    {
        RequireComplete(); await using var transaction = _connection.BeginTransaction();
        string oldKeeper; bool hadSelection;
        await using (var read = _connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT g.keeper, EXISTS(SELECT 1 FROM items i WHERE i.size=g.size AND i.hash=g.hash AND i.removed=0 AND i.selected=1)
                FROM groups g WHERE g.id=$id AND EXISTS
                (SELECT 1 FROM items i WHERE i.path=$path AND i.size=g.size AND i.hash=g.hash AND i.removed=0);
                """;
            read.Parameters.AddWithValue("$id", id); read.Parameters.AddWithValue("$path", path);
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new IOException("Сохраняемый файл не принадлежит группе снимка.");
            oldKeeper = reader.GetString(0); hadSelection = reader.GetInt64(1) != 0;
        }
        await using (var update = _connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE groups SET keeper=$path WHERE id=$id;
                UPDATE items SET selected=CASE
                    WHEN path=$path THEN 0
                    WHEN path=$old AND $had=1 THEN 1
                    ELSE selected END
                WHERE (size,hash)=(SELECT size,hash FROM groups WHERE id=$id) AND removed=0;
                """;
            update.Parameters.AddWithValue("$path", path); update.Parameters.AddWithValue("$old", oldKeeper);
            update.Parameters.AddWithValue("$had", hadSelection ? 1 : 0); update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return 0;
    }, token);

    public Task SetItemSelectedAsync(string path, bool selected, CancellationToken token = default) => RunAsync(async () =>
    {
        RequireComplete(); await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE items SET selected=$selected WHERE path=$path AND removed=0 AND EXISTS
            (SELECT 1 FROM groups g WHERE g.size=items.size AND g.hash=items.hash AND g.keeper<>items.path);
            """;
        command.Parameters.AddWithValue("$selected", selected ? 1 : 0); command.Parameters.AddWithValue("$path", path);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new IOException("Файл недоступен для выбора или является сохраняемой копией.");
        return 0;
    }, token);

    public Task SetAllExtrasSelectedAsync(bool selected, CancellationToken token = default) => RunAsync(async () =>
    {
        RequireComplete(); await using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE items SET selected=$selected WHERE removed=0 AND EXISTS
            (SELECT 1 FROM groups g WHERE g.size=items.size AND g.hash=items.hash AND g.keeper<>items.path);
            UPDATE items SET selected=0 WHERE path IN (SELECT keeper FROM groups);
            """;
        command.Parameters.AddWithValue("$selected", selected ? 1 : 0);
        await command.ExecuteNonQueryAsync(token); return 0;
    }, token);

    public Task ApplyKeeperRuleAsync(DuplicateKeeperRule rule, string? priorityFolder = null,
        CancellationToken token = default) => RunAsync(async () =>
    {
        RequireComplete();
        var order = rule switch
        {
            DuplicateKeeperRule.NewestFile => "i.modified IS NULL, i.modified DESC, length(i.path), i.path COLLATE PHOTOSHELF_PATH",
            DuplicateKeeperRule.OldestFile => "i.modified IS NULL, i.modified ASC, length(i.path), i.path COLLATE PHOTOSHELF_PATH",
            DuplicateKeeperRule.ShortestPath => "length(i.path), i.path COLLATE PHOTOSHELF_PATH",
            DuplicateKeeperRule.PriorityFolder => "CASE WHEN substr(i.path,1,length($slash)) COLLATE PHOTOSHELF_PATH=$slash OR substr(i.path,1,length($backslash)) COLLATE PHOTOSHELF_PATH=$backslash THEN 0 ELSE 1 END, i.modified IS NULL, i.modified DESC, length(i.path), i.path COLLATE PHOTOSHELF_PATH",
            _ => throw new ArgumentOutOfRangeException(nameof(rule))
        };
        string? folder = null;
        if (rule == DuplicateKeeperRule.PriorityFolder)
        {
            if (string.IsNullOrWhiteSpace(priorityFolder) || !Path.IsPathFullyQualified(priorityFolder))
                throw new ArgumentException("Для приоритета нужна абсолютная папка.", nameof(priorityFolder));
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(priorityFolder));
        }
        await using var transaction = _connection.BeginTransaction();
        await using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"""
            CREATE TEMP TABLE keeper_changes(id INTEGER PRIMARY KEY, size INTEGER NOT NULL, hash TEXT NOT NULL,
                old_keeper TEXT NOT NULL COLLATE PHOTOSHELF_PATH, had_selection INTEGER NOT NULL,
                complete_selection INTEGER NOT NULL);
            INSERT INTO keeper_changes(id,size,hash,old_keeper,had_selection,complete_selection)
            SELECT g.id,g.size,g.hash,g.keeper,
                EXISTS(SELECT 1 FROM items s WHERE s.size=g.size AND s.hash=g.hash AND s.removed=0 AND s.selected=1),
                (SELECT count(*) FROM items s WHERE s.size=g.size AND s.hash=g.hash AND s.removed=0 AND s.selected=1)=g.total-1
            FROM groups g;
            UPDATE groups AS g SET keeper=(SELECT i.path FROM items i
                WHERE i.size=g.size AND i.hash=g.hash AND i.removed=0 ORDER BY {order} LIMIT 1);
            UPDATE items SET selected=CASE
                WHEN path=(SELECT keeper FROM groups g WHERE g.size=items.size AND g.hash=items.hash) THEN 0
                WHEN EXISTS(SELECT 1 FROM keeper_changes c WHERE c.size=items.size AND c.hash=items.hash AND c.complete_selection=1) THEN 1
                WHEN path=(SELECT old_keeper FROM keeper_changes c WHERE c.size=items.size AND c.hash=items.hash)
                    AND EXISTS(SELECT 1 FROM keeper_changes c WHERE c.size=items.size AND c.hash=items.hash AND c.had_selection=1) THEN 1
                ELSE selected END
            WHERE removed=0 AND EXISTS(SELECT 1 FROM groups g WHERE g.size=items.size AND g.hash=items.hash);
            DROP TABLE keeper_changes;
            """;
        command.Parameters.AddWithValue("$slash", folder is null ? "" : folder + "/");
        command.Parameters.AddWithValue("$backslash", folder is null ? "" : folder + "\\");
        await command.ExecuteNonQueryAsync(token); await transaction.CommitAsync(token); return 0;
    }, token);

    public Task<DuplicateSelectionSummary> ReadSelectionSummaryAsync(CancellationToken token = default) => RunAsync(async () =>
    {
        RequireComplete(); await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT count(*), count(DISTINCT g.id), coalesce(sum(i.size),0)
            FROM items i JOIN groups g ON g.size=i.size AND g.hash=i.hash
            WHERE i.selected=1 AND i.removed=0 AND i.path<>g.keeper;
            """;
        await using var reader = await command.ExecuteReaderAsync(token); await reader.ReadAsync(token);
        return new DuplicateSelectionSummary(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }, token);

    public Task<IReadOnlyList<DuplicateQuarantineCandidate>> ReadQuarantineSelectionAsync(int maximumFiles = 50_000,
        CancellationToken token = default) => RunAsync<IReadOnlyList<DuplicateQuarantineCandidate>>(async () =>
    {
        RequireComplete(); if (maximumFiles is < 1 or > 50_000) throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT g.id,g.keeper,g.hash,i.path,i.size,i.modified,i.captured
            FROM groups g JOIN items i ON i.size=g.size AND i.hash=g.hash
            WHERE i.selected=1 AND i.removed=0 AND i.path<>g.keeper
            ORDER BY g.id,i.path LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", maximumFiles + 1);
        var result = new List<DuplicateQuarantineCandidate>(Math.Min(maximumFiles, 4096));
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (result.Count == maximumFiles)
                throw new IOException($"За одну операцию можно подготовить не более {maximumFiles:N0} файлов. Снимите часть отметок.");
            result.Add(new DuplicateQuarantineCandidate(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), new SavedMediaItem
            {
                Path = reader.GetString(3), SizeBytes = reader.GetInt64(4),
                FileModifiedAt = reader.IsDBNull(5) ? null : new DateTime(reader.GetInt64(5), DateTimeKind.Local),
                CaptureDate = reader.IsDBNull(6) ? null : new DateTime(reader.GetInt64(6)), MetadataIndexed = true
            }));
        }
        return result;
    }, token);

    public Task<bool> ContainsKeeperAsync(IEnumerable<string> paths, CancellationToken token = default) => RunAsync(async () =>
    {
        RequireComplete(); await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM groups WHERE keeper=$path);";
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested(); command.Parameters.Clear(); command.Parameters.AddWithValue("$path", path);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 0) return true;
        }
        return false;
    }, token);

    public Task MarkMovedAsync(IReadOnlyCollection<string> paths, CancellationToken token = default) => RunAsync(async () =>
    {
        RequireComplete(); if (paths.Count > 50_000) throw new ArgumentOutOfRangeException(nameof(paths));
        await using var transaction = _connection.BeginTransaction();
        await using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            UPDATE groups SET total=total-1 WHERE keeper<>$path
                AND NOT EXISTS(SELECT 1 FROM groups WHERE keeper=$path)
                AND (size,hash)=(SELECT size,hash FROM items WHERE path=$path AND removed=0);
            UPDATE items SET removed=1,selected=0 WHERE path=$path AND NOT EXISTS(SELECT 1 FROM groups WHERE keeper=$path);
            """;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested(); command.Parameters.Clear(); command.Parameters.AddWithValue("$path", path);
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token); return 0;
    }, token);

    private static SavedMediaItem ReadItem(SqliteDataReader reader) => new()
    {
        Path = reader.GetString(0), SizeBytes = reader.GetInt64(1),
        FileModifiedAt = reader.IsDBNull(2) ? null : new DateTime(reader.GetInt64(2), DateTimeKind.Local),
        CaptureDate = reader.IsDBNull(3) ? null : new DateTime(reader.GetInt64(3)), MetadataIndexed = true
    };
    private void RequireComplete() { if (!_completed) throw new InvalidOperationException("Search snapshot is incomplete."); }
    private Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken token) => Task.Run(async () =>
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var interrupt = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(_connection.Handle));
            try { token.ThrowIfCancellationRequested(); return await action(); }
            catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        }
        finally { _gate.Release(); }
    }, token);
    public ValueTask DisposeAsync() => new(Task.Run(async () =>
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return; _disposed = true; await _connection.DisposeAsync();
            // This GUID directory owns only this scratch database. Never touch catalog/journals/media.
            foreach (var name in new[] { "session.sqlite", "session.sqlite-journal", "session.sqlite-wal", "session.sqlite-shm" })
                try { File.Delete(Path.Combine(_directory, name)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(_directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        finally { _gate.Release(); }
    }));
}
