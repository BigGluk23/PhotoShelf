using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoShelf.Application.Files;

public enum CollisionPolicy { Skip, Rename }
public enum FolderLayout { Destination, Year, YearMonth, YearMonthDay }
public enum FileNameStyle { Original, DatePrefix }
public sealed record MoveLayoutOptions(FolderLayout FolderLayout = FolderLayout.Destination,
    FileNameStyle FileNameStyle = FileNameStyle.Original, string? EventName = null);
public sealed record MoveRequest(string Source, DateTime? CaptureDate, IReadOnlyList<string>? ConfirmedCompanions = null);
public sealed record MoveEntry(string Source, string Destination, long Length, DateTime ModifiedUtc, string? SkipReason,
    string? GroupId = null, string? ExpectedHash = null, DateTime? RestoreModifiedUtc = null);
public sealed record MoveResult(MoveEntry Entry, bool Moved, string? Error);
public sealed record MoveJournalEntry(string Source, string Destination, string Staging, string Status, string? Hash,
    string? Error, string? Temporary = null, long Length = 0, DateTime ModifiedUtc = default,
    string? GroupId = null, string? Mode = null, DateTime CreatedUtc = default, string? UndoJournalPath = null,
    IReadOnlyList<MoveJournalEntry>? Members = null, string? RetainedOriginal = null,
    DateTime? DestinationModifiedUtc = null, DateTime? RestoreModifiedUtc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WindowsSecurityDescriptor = null);
public sealed record MoveOperationHistory(string JournalPath, DateTime StartedUtc, int TotalFiles, int CompletedFiles,
    int PendingFiles, bool IsUndone, bool CanUndo, string? UndoJournalPath, string? Error, bool IsLegacy = false,
    bool LegacyReviewed = false, bool HasRecoverableTail = false);

/// <summary>Test seam: production uses durable checkpoints without a callback.</summary>
public sealed class FileMoveOptions
{
    public bool AlwaysCopy { get; init; }
    public JournalPathRelocation? JournalRelocation { get; init; }
    public Func<string, MoveJournalEntry, Task>? Checkpoint { get; init; }
}

/// <summary>Models abrupt process death in fault-injection tests: no in-process cleanup runs.</summary>
public sealed class MoveInterruptionException(string message) : Exception(message);

/// <summary>
/// Never overwrites originals. A catalog callback must be transactional and idempotent: recovery may repeat it.
/// A move group is recoverable, not an atomic filesystem transaction. Errors retain all uncertain copies.
/// Only the operation's own verified staging file may be deleted. Journals must be retained for recovery/undo.
/// </summary>
public sealed class FileMoveService
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private readonly FileMoveOptions _options;
    public FileMoveService(FileMoveOptions? options = null) => _options = options ?? new();

    private string ResolveJournal(string path) => _options.JournalRelocation?.Resolve(path) ?? path;

    public IReadOnlyList<MoveEntry> Plan(IEnumerable<MoveRequest> requests, string destination, CollisionPolicy collision, bool byYear = false, CancellationToken token = default, MoveLayoutOptions? layout = null)
    {
        destination = Path.GetFullPath(destination);
        layout ??= new(byYear ? FolderLayout.YearMonth : FolderLayout.Destination);
        var eventName = ValidateEventName(layout.EventName);
        var reserved = new HashSet<string>(Paths);
        var used = new HashSet<string>(Paths);
        var result = new List<MoveEntry>();
        var directories = new Dictionary<string, CompanionDirectoryIndex>(Paths);
        foreach (var request in requests)
        {
            token.ThrowIfCancellationRequested();
            var source = Path.GetFullPath(request.Source);
            if (used.Contains(source)) continue;
            var groupId = Guid.NewGuid().ToString("N");
            var directory = destination;
            if (layout.FolderLayout != FolderLayout.Destination)
            {
                if (request.CaptureDate is { } date)
                {
                    var culture = CultureInfo.GetCultureInfo("ru-RU");
                    directory = Path.Combine(directory, date.Year.ToString("D4"));
                    if (layout.FolderLayout is FolderLayout.YearMonth or FolderLayout.YearMonthDay)
                        directory = Path.Combine(directory, $"{date.Month:D2} {culture.TextInfo.ToTitleCase(culture.DateTimeFormat.GetMonthName(date.Month))}");
                    if (layout.FolderLayout == FolderLayout.YearMonthDay) directory = Path.Combine(directory, date.ToString("dd"));
                }
                else directory = Path.Combine(directory, "Без даты съёмки");
            }
            if (eventName is not null) directory = Path.Combine(directory, eventName);
            var companions = FindCompanions(source, request.ConfirmedCompanions, directories, token, out var reason);
            if (companions.Count > DurableMoveJournal.MaxGroupMembers || result.Count + companions.Count > DurableMoveJournal.MaxOperationFiles)
                throw new IOException("План превышает безопасный размер операции или связанной группы; разделите выбор, файлы не изменены");
            if (companions.Any(used.Contains)) reason = "Связанный файл уже входит в другую группу";
            var baseStem = Path.GetFileNameWithoutExtension(source);
            var outputStem = layout.FileNameStyle == FileNameStyle.DatePrefix && request.CaptureDate is { } capture
                ? capture.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + "_" + baseStem : baseStem;
            if (Paths.Equals(Path.GetDirectoryName(source), directory) && outputStem == baseStem) reason ??= "Уже в этой папке";
            var number = 0;
            Dictionary<string, string> targets;
            while (true)
            {
                var newStem = number == 0 ? outputStem : $"{outputStem} ({number})";
                targets = companions.ToDictionary(path => path, path => Path.Combine(directory, RenameMember(path, baseStem, newStem)), Paths);
                var conflict = targets.Values.Any(path => File.Exists(path) || Directory.Exists(path) || reserved.Contains(path));
                if (!conflict || collision == CollisionPolicy.Skip || reason is not null) break;
                number++;
            }
            if (targets.Any(pair => Paths.Equals(pair.Key, pair.Value))) reason ??= "Уже в этой папке";
            if (targets.Values.Distinct(Paths).Count() != targets.Count) reason ??= "Неоднозначные имена в связанной группе";
            if (targets.Values.Any(path => File.Exists(path) || Directory.Exists(path) || reserved.Contains(path)))
                reason ??= "Имя занято: пропуск всей связанной группы";
            foreach (var member in companions)
            {
                if (!used.Add(member)) continue;
                long length = 0;
                DateTime modified = default;
                try
                {
                    RejectLinks(member);
                    var info = new FileInfo(member);
                    if (!info.Exists) reason ??= "Исходный файл недоступен";
                    else { length = info.Length; modified = info.LastWriteTimeUtc; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { reason ??= ex.Message; }
                reserved.Add(targets[member]);
                result.Add(new(member, targets[member], length, modified, reason, groupId));
            }
            // Any invalid member blocks every member. A preview must never split a known group.
            if (reason is not null)
                for (var i = result.Count - companions.Count; i < result.Count; i++)
                    if (i >= 0 && result[i].GroupId == groupId) result[i] = result[i] with { SkipReason = reason };
        }
        return result;
    }

    public async Task<IReadOnlyList<MoveResult>> ExecuteAsync(IReadOnlyList<MoveEntry> plan, string journalPath,
        Func<MoveEntry, Task> commitCatalog, IProgress<int>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidatePlanSize(plan);
        await using var journal = DurableMoveJournal.Create(ResolveJournal(journalPath));
        return await ExecuteGroupsAsync(plan, journal, commitCatalog, progress, token);
    }

    public IReadOnlyList<MoveEntry> ReadPlan(string journalPath, CancellationToken token = default)
    {
        var records = Latest(DurableMoveJournal.Read(ResolveJournal(journalPath), token).Entries);
        foreach (var entry in records) { token.ThrowIfCancellationRequested(); ValidateRecord(entry); }
        return records.Select(ToEntry).ToArray();
    }

    public IReadOnlyList<MoveOperationHistory> ReadHistory(string journalDirectory, CancellationToken token = default)
    {
        if (!Directory.Exists(journalDirectory)) return [];
        var history = new List<MoveOperationHistory>();
        foreach (var path in Directory.EnumerateFiles(journalDirectory, "*.jsonl"))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var data = DurableMoveJournal.Read(path, token);
                var records = data.Entries;
                var latest = Latest(records);
                var completed = latest.Count(entry => entry.Status == "completed");
                var undone = latest.Count > 0 && latest.All(entry => entry.Status == "undone");
                history.Add(new(path, File.GetCreationTimeUtc(path), latest.Count, completed,
                    latest.Count(entry => entry.Status is not ("completed" or "undone")), undone,
                    data.TailLength == 0 && completed > 0 && latest.All(entry => entry.Status is "completed" or "undone"),
                    latest.Select(entry => entry.UndoJournalPath is { } link ? ResolveJournal(link) : null).FirstOrDefault(value => value is not null),
                    data.TailLength > 0 ? "Журнал содержит незавершённую запись; требуется проверка/восстановление" :
                    latest.Select(entry => entry.Error).FirstOrDefault(value => value is not null),
                    HasRecoverableTail: data.TailLength > 0 && latest.Count > 0));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { history.Add(LegacyJournalReviewService.TryReadHistory(path, token) ?? new(path, default, 0, 0, 0, false, false, null, ex.Message)); }
        }
        return history.OrderByDescending(entry => entry.StartedUtc).ToArray();
    }

    public async Task<IReadOnlyList<MoveResult>> RecoverAsync(string journalPath, Func<MoveEntry, Task> commitCatalog,
        IProgress<int>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        journalPath = ResolveJournal(journalPath);
        await using var journal = DurableMoveJournal.Open(journalPath, token);
        var records = Latest(journal.Entries);
        var results = new List<MoveResult>();
        var undoHandled = new HashSet<string>(Paths);
        // Undo has its own durable forward operation. Never resume the original in the wrong direction.
        var pendingUndo = records.Where(entry => entry.Status == "undo_pending").ToList();
        var originalsByDestination = new Dictionary<string, MoveJournalEntry>(Paths);
        if (pendingUndo.Count > 0)
            foreach (var record in records)
                if (!originalsByDestination.TryAdd(record.Destination, record))
                    throw new IOException("Неоднозначные целевые пути в исходном журнале; файлы сохранены");
        foreach (var undoGroup in pendingUndo.GroupBy(entry => entry.UndoJournalPath is { } link ? ResolveJournal(link) : null))
        {
            if (undoGroup.Key is null || Paths.Equals(Path.GetFullPath(undoGroup.Key), Path.GetFullPath(journalPath)))
                throw new IOException("Некорректная ссылка на журнал отката");
            var inversePlan = ReadPlan(undoGroup.Key, token);
            // A crash can leave all inverse manifests durable but only some original groups
            // linked to them. Validate the entire inverse before executing any of its entries.
            // Hash/group/path equality prevents a substituted journal from moving unrelated files.
            var matching = new List<MoveJournalEntry>();
            var matchedSources = new HashSet<string>(Paths);
            foreach (var inverse in inversePlan)
            {
                token.ThrowIfCancellationRequested();
                if (!originalsByDestination.TryGetValue(inverse.Source, out var item) || !IsInverseOf(inverse, item)
                    || !(item.Status == "completed" && item.UndoJournalPath is null
                        || (item.Status is "completed" or "undo_pending" or "undone") && item.UndoJournalPath is { } linked && Paths.Equals(ResolveJournal(linked), undoGroup.Key))
                    || !matchedSources.Add(item.Source))
                    throw new IOException("Журнал отката не соответствует исходной операции; файлы сохранены");
                matching.Add(item);
            }
            if (undoGroup.Any(item => !matchedSources.Contains(item.Source)))
                throw new IOException("В журнале отката отсутствует связанный файл; файлы сохранены");
            foreach (var item in matching.Where(item => item.Status == "completed"))
            {
                token.ThrowIfCancellationRequested();
                await journal.AppendAsync(item with { Status = "undo_pending", UndoJournalPath = undoGroup.Key, Error = null });
            }
            var inverseResults = await RecoverAsync(undoGroup.Key, commitCatalog, progress, token);
            var resultsBySource = inverseResults.ToDictionary(result => result.Entry.Source, Paths);
            foreach (var item in matching)
            {
                undoHandled.Add(item.Source);
                resultsBySource.TryGetValue(item.Destination, out var result);
                if (result is not null && !IsInverseOf(result.Entry, item)) throw new IOException("Результат отката не соответствует исходному файлу");
                if (result?.Moved == true)
                {
                    await journal.AppendAsync(item with { Status = "undone", UndoJournalPath = undoGroup.Key, Error = null });
                    results.Add(new(ToEntry(item), true, null));
                }
                else results.Add(new(ToEntry(item), false, result?.Error ?? "Откат требует восстановления"));
            }
        }
        foreach (var group in records.Where(entry => entry.Status != "undo_pending" && !undoHandled.Contains(entry.Source)).GroupBy(entry => entry.GroupId ?? entry.Source))
        {
            token.ThrowIfCancellationRequested();
            var pending = group.Where(entry => entry.Status is not ("completed" or "undone")).ToList();
            results.AddRange(group.Where(entry => entry.Status is "completed" or "undone").Select(entry => new MoveResult(ToEntry(entry), true, null)));
            if (pending.Count == 0) continue;
            var prepared = new List<MoveJournalEntry>();
            try
            {
                foreach (var record in pending)
                {
                    ValidateRecord(record);
                    var state = record;
                    var hasSource = File.Exists(state.Source);
                    var hasStaging = File.Exists(state.Staging);
                    if (hasSource && hasStaging) throw new IOException("Есть и исходный, и временный оригинал; требуется проверка, файлы сохранены");
                    if (hasSource)
                    {
                        await VerifyAsync(state.Source, state, token, sourceSnapshot: true);
                        if (File.Exists(state.Destination)) await VerifyAsync(state.Destination, state, token);
                        await journal.AppendAsync(state with { Status = "recovery_stage_pending", Error = null });
                        NativeRename.MoveExclusive(state.Source, state.Staging);
                        state = state with { Status = "staged", Error = null };
                        await journal.AppendAsync(state);
                    }
                    if (File.Exists(state.Staging)) await VerifyAsync(state.Staging, state, token, sourceSnapshot: true);
                    if (!File.Exists(state.Destination) && !File.Exists(state.Staging))
                        throw new IOException("Ни исходный, ни проверенный целевой файл не найден; автоматическое восстановление остановлено");
                    prepared.Add(state);
                }
                await PublishAndCommitGroupAsync(prepared, journal, commitCatalog, token, recovering: true);
                results.AddRange(pending.Select(entry => new MoveResult(ToEntry(entry), true, null)));
            }
            catch (Exception ex) when (ex is not MoveInterruptionException)
            {
                await RetainAndLogFailureAsync(prepared, journal, ex);
                results.AddRange(pending.Select(entry => new MoveResult(ToEntry(entry), false, ex.Message)));
                if (ex is OperationCanceledException) throw;
            }
            progress?.Report(results.Count);
        }
        return results;
    }

    public async Task<IReadOnlyList<MoveResult>> UndoAsync(string journalPath, string undoJournalPath,
        Func<MoveEntry, Task> commitCatalog, IProgress<int>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        journalPath = ResolveJournal(journalPath);
        undoJournalPath = ResolveJournal(undoJournalPath);
        if (Paths.Equals(Path.GetFullPath(journalPath), Path.GetFullPath(undoJournalPath))) throw new IOException("Откату нужен отдельный журнал");
        await using var original = DurableMoveJournal.Open(journalPath, token);
        var records = Latest(original.Entries);
        if (records.Any(entry => entry.Status is not ("completed" or "undone")))
            throw new IOException("Сначала восстановите незавершённую операцию");
        var available = records.Where(entry => entry.Status == "completed").ToList();
        var availableByDestination = available.ToDictionary(entry => entry.Destination, Paths);
        var inverse = new List<MoveEntry>();
        foreach (var group in available.GroupBy(entry => entry.GroupId ?? entry.Source))
        {
            string? error = null;
            foreach (var item in group)
            {
                try
                {
                    ValidateRecord(item);
                    if (File.Exists(item.Source) || Directory.Exists(item.Source)) throw new IOException("Исходное имя занято; откат не перезаписывает файлы");
                    await VerifyAsync(item.Destination, item, token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= ex.Message; }
            }
            foreach (var item in group)
                inverse.Add(new(item.Destination, item.Source, item.Length, File.GetLastWriteTimeUtc(item.Destination), error, item.GroupId, item.Hash, item.ModifiedUtc));
        }
        // Create the inverse journal first. Its full plan is durable before the original points to it.
        await using var undo = DurableMoveJournal.Create(undoJournalPath);
        var validInverse = inverse.Where(entry => entry.SkipReason is null).ToArray();
        ValidatePlanSize(validInverse);
        foreach (var inverseGroup in validInverse.GroupBy(entry => entry.GroupId ?? entry.Source))
        {
            token.ThrowIfCancellationRequested();
            var undoMembers = inverseGroup.Select(entry => NewState(entry) with { Hash = entry.ExpectedHash, GroupId = entry.GroupId, Status = "planned" }).ToArray();
            await undo.AppendAsync(undoMembers[0] with { Status = "group_manifest", Members = undoMembers });
        }
        foreach (var inverseGroup in validInverse.GroupBy(entry => entry.GroupId ?? entry.Source))
        {
            token.ThrowIfCancellationRequested();
            var originals = inverseGroup.Select(entry => availableByDestination[entry.Source]
                with { Status = "undo_pending", UndoJournalPath = Path.GetFullPath(undoJournalPath), Error = null }).ToArray();
            await original.AppendAsync(originals[0] with { Status = "undo_manifest", Members = originals });
            await Checkpoint("undo_manifest_written", originals[0]);
        }
        // Execute through recovery to reuse the durable staging names written above.
        await undo.DisposeAsync();
        var completed = inverse.Any(entry => entry.SkipReason is null)
            ? await RecoverAsync(undoJournalPath, commitCatalog, progress, token)
            : Array.Empty<MoveResult>();
        foreach (var result in completed.Where(result => result.Moved))
        {
            var originalEntry = availableByDestination[result.Entry.Source];
            await original.AppendAsync(originalEntry with { Status = "undone", UndoJournalPath = Path.GetFullPath(undoJournalPath), Error = null });
        }
        return completed.Concat(inverse.Where(entry => entry.SkipReason is not null).Select(entry => new MoveResult(entry, false, entry.SkipReason))).ToArray();
    }

    private async Task<IReadOnlyList<MoveResult>> ExecuteGroupsAsync(IReadOnlyList<MoveEntry> plan, DurableMoveJournal journal,
        Func<MoveEntry, Task> commitCatalog, IProgress<int>? progress, CancellationToken token)
    {
        ValidatePlanSize(plan);
        var results = new List<MoveResult>();
        if (plan.Where(entry => entry.SkipReason is null).GroupBy(entry => Path.GetFullPath(entry.Destination), Paths).Any(group => group.Count() > 1))
            throw new IOException("Несколько файлов направлены в один путь; операция остановлена");
        if (plan.Where(entry => entry.SkipReason is null).GroupBy(entry => Path.GetFullPath(entry.Source), Paths).Any(group => group.Count() > 1))
            throw new IOException("Исходный файл повторяется в плане; операция остановлена");
        var allSources = plan.Select(entry => Path.GetFullPath(entry.Source)).ToHashSet(Paths);
        if (plan.Where(entry => entry.SkipReason is null).Any(entry => allSources.Contains(Path.GetFullPath(entry.Destination))))
            throw new IOException("Целевой путь совпадает с исходным файлом другой операции");
        foreach (var group in plan.GroupBy(entry => entry.GroupId ?? entry.Source))
        {
            token.ThrowIfCancellationRequested();
            var members = group.ToList();
            var skip = members.Select(entry => entry.SkipReason).FirstOrDefault(reason => reason is not null);
            if (skip is not null)
            {
                results.AddRange(members.Select(entry => new MoveResult(entry, false, skip)));
                progress?.Report(results.Count);
                continue;
            }
            var prepared = new List<MoveJournalEntry>();
            var manifestPersisted = false;
            try
            {
                foreach (var entry in members) Preflight(entry);
                // Hash and journal all members before moving any. Originals remain readable after interruption.
                foreach (var entry in members)
                {
                    await using var input = OpenLockedRead(entry.Source);
                    CheckSnapshot(entry);
                    var digest = await HashAsync(input, token);
                    CheckSnapshot(entry);
                    if (entry.ExpectedHash is not null && digest != entry.ExpectedHash) throw new IOException("Содержимое файла изменилось");
                    var state = NewState(entry) with { Hash = digest, Status = "planned" };
                    prepared.Add(state);
                }
                await journal.AppendAsync(prepared[0] with { Status = "group_manifest", Members = prepared.ToArray() });
                manifestPersisted = true;
                foreach (var state in prepared) await Checkpoint("planned", state);
                for (var i = 0; i < prepared.Count; i++)
                {
                    var state = prepared[i];
                    token.ThrowIfCancellationRequested();
                    // Revalidate bytes after releasing the preparation handle, before taking ownership.
                    await VerifyAsync(state.Source, state, token, sourceSnapshot: true);
                    NativeRename.MoveExclusive(state.Source, state.Staging);
                    state = state with { Status = "staged" };
                    prepared[i] = state;
                    await journal.AppendAsync(state);
                    await Checkpoint("staged", state);
                }
                await PublishAndCommitGroupAsync(prepared, journal, commitCatalog, token);
                results.AddRange(members.Select(entry => new MoveResult(entry, true, null)));
            }
            catch (Exception ex) when (ex is not MoveInterruptionException)
            {
                if (manifestPersisted) await RetainAndLogFailureAsync(prepared, journal, ex);
                results.AddRange(members.Select(entry => new MoveResult(entry, false, ex.Message)));
                if (ex is OperationCanceledException) throw;
            }
            progress?.Report(results.Count);
        }
        return results;
    }

    private async Task PublishAndCommitGroupAsync(List<MoveJournalEntry> states, DurableMoveJournal journal,
        Func<MoveEntry, Task> commitCatalog, CancellationToken token, bool recovering = false)
    {
        for (var i = 0; i < states.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var state = states[i];
            RejectLinks(state.Destination);
            Directory.CreateDirectory(Path.GetDirectoryName(state.Destination)!);
            RejectLinks(state.Destination);
            if (File.Exists(state.Destination))
            {
                // Only recovery can adopt an already published target; normal execution always rejects a racing name.
                if (!recovering) throw new IOException("Целевое имя появилось после предварительного просмотра");
                if (OperatingSystem.IsWindows() && state.Mode == "copy" && state.WindowsSecurityDescriptor is null)
                {
                    // Legacy recovery may derive permissions only from the original that
                    // still exists. The possibly broader destination is never the authority.
                    if (!File.Exists(state.Staging)) throw new IOException("У старой копии нет снимка ACL и исходного файла; требуется ручное согласование прав");
                    await using var source = OpenLockedRead(state.Staging);
                    state = state with { WindowsSecurityDescriptor = WindowsFileSecurity.Capture(source.SafeFileHandle) };
                    states[i] = state; await journal.AppendAsync(state);
                }
                await VerifyAsync(state.Destination, state, token);
                states[i] = state with { DestinationModifiedUtc = File.GetLastWriteTimeUtc(state.Destination) };
                continue;
            }
            await VerifyAsync(state.Staging, state, token, sourceSnapshot: true);
            var renamed = false;
            if (!_options.AlwaysCopy)
            {
                state = state with { Status = "rename_pending", Mode = "rename" };
                states[i] = state;
                await journal.AppendAsync(state);
                await Checkpoint("rename_pending", state);
                // Native rename has NO copy fallback and NO overwrite flag. Cross-volume attempts safely fail.
                await using var original = new FileStream(state.Staging, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (original.Length != state.Length || await HashAsync(original, token) != state.Hash)
                    throw new IOException("Исходный файл изменился перед переименованием");
                renamed = NativeRename.TryMove(state.Staging, state.Destination);
            }
            if (!renamed)
            {
                CheckAvailableSpace(state.Destination, state.Length);
                // A byte stream copy must never discard NTFS alternate streams or EFS protection.
                WindowsFileStreams.RequirePlainFile(state.Staging);
                state = state with { Status = "copying", Mode = "copy" };
                states[i] = state;
                await journal.AppendAsync(state);
                await Checkpoint("copying", state);
                // A partial temporary copy is never overwritten or silently deleted. Give this attempt a new name.
                if (File.Exists(state.Temporary))
                {
                    state = state with { Temporary = state.Destination + ".photoshelf-copy-" + Guid.NewGuid().ToString("N") };
                    states[i] = state;
                    await journal.AppendAsync(state);
                }
                await using (var input = OpenLockedRead(state.Staging))
                {
                    if (OperatingSystem.IsWindows())
                    {
                        if (state.WindowsSecurityDescriptor is null)
                        {
                            state = state with { WindowsSecurityDescriptor = WindowsFileSecurity.Capture(input.SafeFileHandle) };
                            states[i] = state;
                            await journal.AppendAsync(state); // Durable before the first destination byte exists.
                        }
                        WindowsFileSecurity.Verify(input.SafeFileHandle, state.WindowsSecurityDescriptor, requireProtected: false);
                    }
                    await using (var output = OperatingSystem.IsWindows()
                        ? WindowsFileSecurity.CreateRestrictedCopy(state.Temporary!, state.WindowsSecurityDescriptor!)
                        : new FileStream(state.Temporary!, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                            131072, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
                    {
                        await Checkpoint("copy_created", state);
                        if (OperatingSystem.IsWindows()) WindowsFileSecurity.Verify(output.SafeFileHandle, state.WindowsSecurityDescriptor, requireProtected: true);
                        await input.CopyToAsync(output, token);
                        output.Flush(true);
                    }
                    File.SetLastWriteTimeUtc(state.Temporary!, state.RestoreModifiedUtc ?? state.ModifiedUtc);
                    if (state.CreatedUtc != default) File.SetCreationTimeUtc(state.Temporary!, state.CreatedUtc);
                    using (var metadata = new FileStream(state.Temporary!, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) metadata.Flush(true);
                    await Checkpoint("copy_written", state);
                    if (await HashAsync(input, token) != state.Hash) throw new IOException("Исходный файл изменился при копировании");
                    await VerifyAsync(state.Temporary!, state, token);
                }
                state = state with { Status = "copy_verified" };
                states[i] = state;
                await journal.AppendAsync(state);
                await Checkpoint("copy_verified", state);
                NativeRename.MoveExclusive(state.Temporary!, state.Destination);
            }
            if (renamed && state.RestoreModifiedUtc is { } restoredTime)
                File.SetLastWriteTimeUtc(state.Destination, restoredTime);
            state = state with { Status = "destination_verified", DestinationModifiedUtc = File.GetLastWriteTimeUtc(state.Destination) };
            states[i] = state;
            // Read from the final path, including the rename case. Never infer intact bytes from a successful rename.
            await VerifyAsync(state.Destination, state, token);
            await journal.AppendAsync(state);
            await Checkpoint("destination_verified", state);
        }
        // From this boundary, cancellation is deferred until the complete group is durable.
        // Hold all target read handles to deny writes/deletion during catalog commit and source cleanup.
        var targets = new List<FileStream>();
        try
        {
            foreach (var state in states)
            {
                var stream = OpenLockedRead(state.Destination);
                targets.Add(stream);
                if (stream.Length != state.Length || await HashAsync(stream, CancellationToken.None) != state.Hash)
                    throw new IOException("Целевая копия изменилась; исходный файл сохранён");
                if (OperatingSystem.IsWindows() && state.Mode == "copy")
                    WindowsFileSecurity.Verify(stream.SafeFileHandle, state.WindowsSecurityDescriptor, requireProtected: true);
            }
            foreach (var state in states)
            {
                await commitCatalog(ToEntry(state));
                await journal.AppendAsync(state with { Status = "catalog_committed", Error = null });
                await Checkpoint("catalog_committed", state);
            }
            // All companions have a verified destination and a durable catalog checkpoint before any copy is removed.
            for (var i = 0; i < states.Count; i++)
            {
                var state = states[i];
                await Checkpoint("before_source_cleanup", state);
                if (File.Exists(state.Staging))
                {
                    RejectLinks(state.Staging);
                    if (OperatingSystem.IsWindows())
                    {
                        // DELETE access belongs to this handle; sharing denies replacement, deletion and writes.
                        // Disposition applies to the verified file object, never to a subsequently resolved path.
                        using var handle = WindowsFileRemoval.Open(state.Staging);
                        await using var original = new FileStream(handle, FileAccess.Read, 131072, false);
                        WindowsFileStreams.RequirePlainFile(state.Staging);
                        if (original.Length != state.Length || File.GetLastWriteTimeUtc(state.Staging) != state.ModifiedUtc || await HashAsync(original, CancellationToken.None) != state.Hash)
                            throw new IOException("Временный оригинал изменился; все копии сохранены");
                        if (state.Mode == "copy")
                        {
                            WindowsFileSecurity.Verify(original.SafeFileHandle, state.WindowsSecurityDescriptor, requireProtected: false);
                            WindowsFileSecurity.Verify(targets[i].SafeFileHandle, state.WindowsSecurityDescriptor, requireProtected: true);
                        }
                        await Checkpoint("source_locked_for_cleanup", state);
                        WindowsFileRemoval.MarkForDeletion(handle);
                    }
                    else
                    {
                        // POSIX unlink is path-based, and cannot safely delete a verified open file object.
                        // The Windows app uses the branch above. Portable tests retain a journaled backup.
                        await VerifyAsync(state.Staging, state, CancellationToken.None, sourceSnapshot: true);
                        state = state with { RetainedOriginal = state.Source + ".photoshelf-retained-" + Guid.NewGuid().ToString("N") };
                        states[i] = state;
                        await journal.AppendAsync(state with { Status = "retain_original_pending" });
                        await Checkpoint("source_locked_for_cleanup", state);
                        NativeRename.MoveExclusive(state.Staging, state.RetainedOriginal);
                        await VerifyAsync(state.RetainedOriginal, state, CancellationToken.None);
                    }
                }
                await Checkpoint("source_removed", state);
                await journal.AppendAsync(state with { Status = "completed", Error = null });
                await Checkpoint("completed", state);
            }
        }
        finally { foreach (var stream in targets) await stream.DisposeAsync(); }
    }

    private async Task RetainAndLogFailureAsync(IEnumerable<MoveJournalEntry> states, DurableMoveJournal journal, Exception error)
    {
        foreach (var state in states)
        {
            string? restoreError = null;
            // Restore only our intact staging original, without touching a published destination.
            if (File.Exists(state.Staging) && !File.Exists(state.Source))
            {
                try
                {
                    await VerifyAsync(state.Staging, state, CancellationToken.None, sourceSnapshot: true);
                    RejectLinks(state.Source);
                    NativeRename.MoveExclusive(state.Staging, state.Source);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { restoreError = ex.Message; }
            }
            await journal.AppendAsync(state with { Status = "reconciliation_required",
                Error = restoreError is null ? error.Message : $"{error.Message}; оригинал сохранён во временном пути: {restoreError}" });
        }
    }

    private Task Checkpoint(string phase, MoveJournalEntry state) => _options.Checkpoint?.Invoke(phase, state) ?? Task.CompletedTask;
    private static void ValidatePlanSize(IReadOnlyList<MoveEntry> plan)
    {
        if (plan.Count > DurableMoveJournal.MaxOperationFiles || plan.GroupBy(entry => entry.GroupId ?? entry.Source).Any(group => group.Count() > DurableMoveJournal.MaxGroupMembers))
            throw new IOException("План превышает безопасный размер операции или связанной группы; файлы не изменены");
    }
    private static MoveJournalEntry NewState(MoveEntry entry) => new(entry.Source, entry.Destination,
        entry.Source + ".photoshelf-moving-" + Guid.NewGuid().ToString("N"), "planned", null, null,
        entry.Destination + ".photoshelf-copy-" + Guid.NewGuid().ToString("N"), entry.Length, entry.ModifiedUtc,
        entry.GroupId ?? Guid.NewGuid().ToString("N"), null, File.GetCreationTimeUtc(entry.Source), RestoreModifiedUtc: entry.RestoreModifiedUtc);
    private static MoveEntry ToEntry(MoveJournalEntry state) => new(state.Source, state.Destination, state.Length, state.ModifiedUtc, null, state.GroupId, state.Hash, state.RestoreModifiedUtc);
    private static bool IsInverseOf(MoveEntry inverse, MoveJournalEntry original) =>
        Paths.Equals(inverse.Source, original.Destination) && Paths.Equals(inverse.Destination, original.Source)
        && inverse.Length == original.Length && inverse.ExpectedHash is not null && inverse.ExpectedHash == original.Hash
        && inverse.GroupId == original.GroupId;
    private static List<MoveJournalEntry> Latest(IEnumerable<MoveJournalEntry> records) => records
        .SelectMany(entry => entry.Members ?? new[] { entry }).GroupBy(entry => entry.Source, Paths).Select(group => group.Last()).ToList();
    private static FileStream OpenLockedRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static async Task<string> HashAsync(FileStream stream, CancellationToken token)
    {
        stream.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
    private static async Task VerifyAsync(string path, MoveJournalEntry state, CancellationToken token, bool sourceSnapshot = false)
    {
        RejectLinks(path);
        await using var stream = OpenLockedRead(path);
        var expectedTime = sourceSnapshot ? state.ModifiedUtc : Paths.Equals(path, state.Destination) ? state.DestinationModifiedUtc : null;
        if (state.Hash is null || stream.Length != state.Length || (expectedTime is { } time && File.GetLastWriteTimeUtc(path) != time)
            || await HashAsync(stream, token) != state.Hash)
            throw new IOException($"Контрольная сумма не совпала: {Path.GetFileName(path)}. Файл сохранён для проверки");
    }
    private static void CheckAvailableSpace(string destination, long length)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(destination)!);
            if (drive.IsReady && drive.AvailableFreeSpace < length)
                throw new IOException("Недостаточно свободного места для проверенной копии; исходный файл сохранён");
        }
        catch (ArgumentException) { /* Some UNC roots cannot expose free-space information. Copy still handles ENOSPC safely. */ }
    }
    private static void CheckSnapshot(MoveEntry entry)
    {
        var info = new FileInfo(entry.Source);
        if (!info.Exists || info.Length != entry.Length || info.LastWriteTimeUtc != entry.ModifiedUtc)
            throw new IOException("Файл изменился после предварительного просмотра");
    }
    private static void Preflight(MoveEntry entry)
    {
        if (!Path.IsPathFullyQualified(entry.Source) || !Path.IsPathFullyQualified(entry.Destination)) throw new IOException("Нужны абсолютные пути");
        RejectLinks(entry.Source);
        RejectLinks(entry.Destination);
        CheckSnapshot(entry);
        if (File.Exists(entry.Destination) || Directory.Exists(entry.Destination)) throw new IOException("Целевое имя уже занято");
        if (Paths.Equals(entry.Source, entry.Destination)) throw new IOException("Исходный и целевой пути совпадают");
    }
    private static void ValidateRecord(MoveJournalEntry state)
    {
        if (state.Hash is null || state.Hash.Length != 64 || state.Length < 0 || !Path.IsPathFullyQualified(state.Source)
            || !Path.IsPathFullyQualified(state.Destination) || !Path.IsPathFullyQualified(state.Staging)
            || !state.Staging.StartsWith(state.Source + ".photoshelf-moving-", StringComparison.Ordinal)
            || state.Temporary is null || !state.Temporary.StartsWith(state.Destination + ".photoshelf-copy-", StringComparison.Ordinal)
            || Paths.Equals(state.Source, state.Destination))
            throw new IOException("Журнал не содержит достаточно проверенных данных для безопасного восстановления");
        RejectLinks(state.Source); RejectLinks(state.Destination); RejectLinks(state.Staging);
    }
    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Перенос через символические ссылки или точки подключения требует отдельного подтверждённого пути");
        }
    }
    private static List<string> FindCompanions(string source, IReadOnlyList<string>? confirmed,
        Dictionary<string, CompanionDirectoryIndex> directories, CancellationToken token, out string? reason)
    {
        reason = null;
        var result = new HashSet<string>(Paths) { source };
        var directory = Path.GetDirectoryName(source)!;
        if (!Directory.Exists(directory)) return result.ToList();
        var stem = Path.GetFileNameWithoutExtension(source);
        List<string> candidates;
        CompanionDirectoryIndex index;
        try
        {
            if (!directories.TryGetValue(directory, out index!))
            {
                index = new CompanionDirectoryIndex();
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    var memberStem = Path.GetFileNameWithoutExtension(path);
                    if (!index.ByStem.TryGetValue(memberStem, out var members)) index.ByStem[memberStem] = members = [];
                    members.Add(path);
                    index.ByName[Path.GetFileName(path)] = path;
                }
                directories.Add(directory, index);
            }
            candidates = index.ByStem.TryGetValue(stem, out var matching) ? matching.ToList() : [];
            foreach (var extension in new[] { ".xmp", ".aae" })
                if (index.ByName.TryGetValue(Path.GetFileName(source) + extension, out var sidecar)) candidates.Add(sidecar);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { reason = ex.Message; return result.ToList(); }
        if (confirmed is not null)
        {
            foreach (var companion in confirmed)
            {
                var path = Path.GetFullPath(companion);
                if (!Paths.Equals(Path.GetDirectoryName(path), directory)) reason ??= "Связанные файлы из разных папок требуют отдельного плана";
                result.Add(path);
            }
        }
        var primary = candidates.Where(path => PhotoShelf.Domain.MediaFormatRegistry.IsPhoto(path)).ToList();
        var raws = primary.Where(path => PhotoShelf.Domain.MediaFormatRegistry.IsRaw(path)).ToList();
        var jpegs = primary.Where(path => Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)).ToList();
        if (primary.Count > 1)
        {
            if (primary.Count == 2 && raws.Count == 1 && jpegs.Count == 1) foreach (var path in primary) result.Add(path);
            else if (!primary.All(result.Contains)) reason ??= "Неоднозначная группа изображений с одинаковым именем";
        }
        var movs = candidates.Where(path => Path.GetExtension(path).Equals(".mov", StringComparison.OrdinalIgnoreCase)).ToList();
        if (primary.Count > 0 && movs.Count > 0 && !primary.Concat(movs).All(result.Contains))
            reason ??= "Фото и MOV могут быть Live Photo: связь должна быть подтверждена идентификатором";
        // Exact sidecars for every known primary, including file.ext.xmp convention.
        foreach (var path in candidates.Where(path => Path.GetExtension(path).Equals(".xmp", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".aae", StringComparison.OrdinalIgnoreCase))) result.Add(path);
        foreach (var member in result.ToArray())
        {
            foreach (var extension in new[] { ".xmp", ".aae" })
                if (index.ByName.TryGetValue(Path.GetFileName(member) + extension, out var namedSidecar)) result.Add(namedSidecar);
        }
        // An unclassified same-stem member might contain unique media or metadata.
        // It must be explicitly confirmed, never silently left behind by an automatic move.
        if (candidates.Any(path => !result.Contains(path)))
            reason ??= "Одноимённые связанные файлы не подтверждены: требуется разбор всей группы";
        return result.OrderBy(path => Paths.Equals(path, source) ? 0 : 1).ThenBy(path => path, Paths).ToList();
    }
    private static string? ValidateEventName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var name = value.Trim();
        var stem = name.Split('.')[0];
        if (name is "." or ".." || name.EndsWith('.') || name.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0
            || name.Any(char.IsControl) || new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem, Paths)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem[3] is >= '1' and <= '9'))
            throw new ArgumentException("Название события должно быть одним допустимым именем папки Windows", nameof(value));
        return name;
    }
    private sealed class CompanionDirectoryIndex
    {
        public Dictionary<string, List<string>> ByStem { get; } = new(Paths);
        public Dictionary<string, string> ByName { get; } = new(Paths);
    }
    private static string RenameMember(string path, string oldStem, string newStem)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(oldStem + ".", StringComparison.OrdinalIgnoreCase) ? newStem + name[oldStem.Length..] : name;
    }
}
