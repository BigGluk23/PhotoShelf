using System.Security.Cryptography;
using PhotoShelf.Application.Files;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class FileMoveRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "photoshelf-recovery-test-" + Guid.NewGuid().ToString("N"));
    private string Journal => Path.Combine(_root, "operations", "move.jsonl");
    private string Out => Path.Combine(_root, "out");
    public FileMoveRecoveryTests() => Directory.CreateDirectory(_root);
    private string Write(string name, string content = "unique original bytes")
    {
        var path = Path.GetFullPath(Path.Combine(_root, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
    private static FileMoveService Service(bool copy = true, Func<string, MoveJournalEntry, Task>? hook = null) =>
        new(new FileMoveOptions { AlwaysCopy = copy, Checkpoint = hook });
    private IReadOnlyList<MoveEntry> Plan(FileMoveService service, string source) => service.Plan([new(source, null)], Out, CollisionPolicy.Skip);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private Task Commit(MoveEntry entry) { Assert.True(File.Exists(entry.Destination)); return Task.CompletedTask; }

    [Theory]
    [InlineData("planned")]
    [InlineData("staged")]
    [InlineData("copying")]
    [InlineData("copy_written")]
    [InlineData("copy_verified")]
    [InlineData("destination_verified")]
    [InlineData("catalog_committed")]
    [InlineData("before_source_cleanup")]
    [InlineData("source_removed")]
    [InlineData("completed")]
    public async Task CrashAtEveryCopyBoundaryRecoversOriginalBytesIdempotently(string phase)
    {
        var source = Write("in/фото 🦒.jpg");
        var digest = Hash(source);
        var service = Service(hook: (at, _) => at == phase ? throw new MoveInterruptionException(at) : Task.CompletedTask);
        var plan = Plan(service, source);
        await Assert.ThrowsAsync<MoveInterruptionException>(() => service.ExecuteAsync(plan, Journal, Commit, null, default));
        Assert.Contains(Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Where(path => !path.Contains("operations")), path => Hash(path) == digest);
        var recovered = await Service().RecoverAsync(Journal, Commit, null, default);
        Assert.All(recovered, result => Assert.True(result.Moved, result.Error));
        Assert.Equal(digest, Hash(plan[0].Destination));
        Assert.False(File.Exists(source));
        Assert.DoesNotContain(Directory.GetFiles(_root, "*.photoshelf-moving-*", SearchOption.AllDirectories), _ => true);
        var again = await Service().RecoverAsync(Journal, _ => throw new Exception("Completed callbacks must not repeat"), null, default);
        Assert.All(again, result => Assert.True(result.Moved, result.Error));
        var history = Assert.Single(Service().ReadHistory(Path.GetDirectoryName(Journal)!));
        Assert.Equal(1, history.CompletedFiles); Assert.Equal(0, history.PendingFiles); Assert.True(history.CanUndo);
    }

    [Theory]
    [InlineData("rename_pending")]
    [InlineData("destination_verified")]
    [InlineData("catalog_committed")]
    [InlineData("source_removed")]
    public async Task SameVolumeRenameRecoversWithoutCopying(string phase)
    {
        var source = Write("in/p.jpg");
        var service = Service(false, (at, _) => at == phase ? throw new MoveInterruptionException(at) : Task.CompletedTask);
        var plan = Plan(service, source);
        await Assert.ThrowsAsync<MoveInterruptionException>(() => service.ExecuteAsync(plan, Journal, Commit, null, default));
        var results = await Service(false).RecoverAsync(Journal, Commit, null, default);
        Assert.All(results, result => Assert.True(result.Moved, result.Error));
        Assert.Equal("unique original bytes", File.ReadAllText(plan[0].Destination));
        Assert.Empty(Directory.GetFiles(_root, "*.photoshelf-copy-*", SearchOption.AllDirectories));
    }

    [Fact] public async Task CorruptTemporaryCopyNeverRemovesOriginal()
    {
        var source = Write("in/p.jpg");
        var service = Service(hook: (phase, entry) =>
        {
            if (phase == "copy_written") File.WriteAllText(entry.Temporary!, "corrupt");
            return Task.CompletedTask;
        });
        var plan = Plan(service, source);
        var result = Assert.Single(await service.ExecuteAsync(plan, Journal, Commit, null, default));
        Assert.False(result.Moved); Assert.Equal("unique original bytes", File.ReadAllText(source));
        Assert.False(File.Exists(plan[0].Destination));
        var recovered = Assert.Single(await Service().RecoverAsync(Journal, Commit, null, default));
        Assert.True(recovered.Moved, recovered.Error);
        Assert.Equal("unique original bytes", File.ReadAllText(plan[0].Destination));
        Assert.Contains(Directory.GetFiles(Out, "*.photoshelf-copy-*"), path => File.ReadAllText(path) == "corrupt");
    }

    [Fact] public async Task SourceChangeAfterHashIsDetectedAndNewBytesArePreserved()
    {
        var source = Write("in/p.jpg");
        var service = Service(hook: (phase, entry) =>
        {
            if (phase == "staged") File.WriteAllText(entry.Staging, "externally changed");
            return Task.CompletedTask;
        });
        var result = Assert.Single(await service.ExecuteAsync(Plan(service, source), Journal, Commit, null, default));
        Assert.False(result.Moved);
        Assert.Equal("externally changed", File.ReadAllText(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(source)!, "*.photoshelf-moving-*"))));
        Assert.False(File.Exists(Path.Combine(Out, "p.jpg")));
        var recovery = Assert.Single(await Service().RecoverAsync(Journal, Commit, null, default));
        Assert.False(recovery.Moved);
    }

    [Fact] public async Task DestinationRaceEvenWithIdenticalBytesNeverAdoptsForeignFile()
    {
        var source = Write("in/p.jpg");
        var service = Service(hook: (phase, entry) =>
        {
            if (phase == "staged") Write("out/p.jpg");
            return Task.CompletedTask;
        });
        var result = Assert.Single(await service.ExecuteAsync(Plan(service, source), Journal, _ => throw new Exception("no commit"), null, default));
        Assert.False(result.Moved); Assert.True(File.Exists(source)); Assert.True(File.Exists(Path.Combine(Out, "p.jpg")));
    }

    [Fact] public async Task CatalogFailureCanBeRecoveredWithRepeatedIdempotentCommit()
    {
        var source = Write("in/p.jpg"); var service = Service(); var plan = Plan(service, source);
        var callbackCount = 0;
        var result = Assert.Single(await service.ExecuteAsync(plan, Journal, _ => { callbackCount++; throw new IOException("sqlite busy after commit"); }, null, default));
        Assert.False(result.Moved); Assert.True(File.Exists(source)); Assert.True(File.Exists(plan[0].Destination));
        var recovered = Assert.Single(await service.RecoverAsync(Journal, _ => { callbackCount++; return Task.CompletedTask; }, null, default));
        Assert.True(recovered.Moved, recovered.Error); Assert.Equal(2, callbackCount); Assert.False(File.Exists(source));
    }

    [Fact] public async Task CancellationDuringCopyRestoresOriginalAndCanBeResumed()
    {
        using var cancellation = new CancellationTokenSource();
        var source = Write("in/p.jpg");
        var service = Service(hook: (phase, _) => { if (phase == "copying") cancellation.Cancel(); return Task.CompletedTask; });
        var plan = Plan(service, source);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync(plan, Journal, Commit, null, cancellation.Token));
        Assert.Equal("unique original bytes", File.ReadAllText(source));
        var result = Assert.Single(await Service().RecoverAsync(Journal, Commit, null, default));
        Assert.True(result.Moved, result.Error);
    }

    [Fact] public async Task DeniedWriteSimulatesDiskFailureAndRetainsOriginal()
    {
        var source = Write("in/p.jpg");
        var service = Service(hook: (phase, _) => phase == "copy_written" ? throw new IOException("Disk full / flush error") : Task.CompletedTask);
        var result = Assert.Single(await service.ExecuteAsync(Plan(service, source), Journal, Commit, null, default));
        Assert.False(result.Moved); Assert.Equal("unique original bytes", File.ReadAllText(source));
    }

    [Fact] public async Task LockedSourceIsNeverMoved()
    {
        var source = Write("in/p.jpg"); var service = Service(); var plan = Plan(service, source);
        await using var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = Assert.Single(await service.ExecuteAsync(plan, Journal, Commit, null, default));
        Assert.False(result.Moved); Assert.True(File.Exists(source)); Assert.False(File.Exists(plan[0].Destination));
    }

    [Fact] public async Task SidecarGroupIsDurableBeforeFirstStagingAndNamesStayAligned()
    {
        var raw = Write("in/p.dng", "raw"); var jpeg = Write("in/p.jpg", "jpeg"); var xmp = Write("in/p.xmp", "metadata");
        Write("out/p.jpg", "existing");
        var service = Service(hook: (phase, _) => phase == "staged" ? throw new MoveInterruptionException(phase) : Task.CompletedTask);
        var plan = service.Plan([new(raw, null), new(jpeg, null)], Out, CollisionPolicy.Rename);
        Assert.Equal(3, plan.Count); Assert.All(plan, entry => Assert.Null(entry.SkipReason));
        Assert.All(plan, entry => Assert.StartsWith("p (1).", Path.GetFileName(entry.Destination)));
        await Assert.ThrowsAsync<MoveInterruptionException>(() => service.ExecuteAsync(plan, Journal, Commit, null, default));
        var results = await Service().RecoverAsync(Journal, Commit, null, default);
        Assert.Equal(3, results.Count); Assert.All(results, result => Assert.True(result.Moved, result.Error));
        Assert.Equal("raw", File.ReadAllText(Path.Combine(Out, "p (1).dng")));
        Assert.Equal("jpeg", File.ReadAllText(Path.Combine(Out, "p (1).jpg")));
        Assert.Equal("metadata", File.ReadAllText(Path.Combine(Out, "p (1).xmp")));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(Out, "p.jpg")));
        Assert.False(File.Exists(raw)); Assert.False(File.Exists(jpeg)); Assert.False(File.Exists(xmp));
    }

    [Fact] public void AmbiguousLivePhotoIsBlockedUntilItsPairingIsConfirmed()
    {
        var heic = Write("in/p.heic"); var mov = Write("in/p.mov"); var aae = Write("in/p.aae");
        var service = Service();
        Assert.All(Plan(service, heic), entry => Assert.NotNull(entry.SkipReason));
        var confirmed = service.Plan([new(heic, null, [mov])], Out, CollisionPolicy.Skip);
        Assert.Equal(3, confirmed.Count); Assert.All(confirmed, entry => Assert.Null(entry.SkipReason));
        Assert.Contains(confirmed, entry => entry.Source == aae);
    }

    [Fact] public async Task UndoChecksContentAndRestoresGroupWithMetadata()
    {
        var source = Write("in/p.jpg"); Write("in/p.xmp", "tags");
        var service = Service(); var plan = Plan(service, source);
        var modified = plan[0].ModifiedUtc;
        Assert.All(await service.ExecuteAsync(plan, Journal, Commit, null, default), result => Assert.True(result.Moved, result.Error));
        var results = await service.UndoAsync(Journal, Path.Combine(_root, "operations", "undo.jsonl"), Commit, null, default);
        Assert.Equal(2, results.Count); Assert.All(results, result => Assert.True(result.Moved, result.Error));
        Assert.Equal("unique original bytes", File.ReadAllText(source)); Assert.Equal("tags", File.ReadAllText(Path.Combine(_root, "in", "p.xmp")));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(source)); Assert.False(File.Exists(plan[0].Destination));
        var history = service.ReadHistory(Path.GetDirectoryName(Journal)!);
        Assert.True(history.Single(item => item.JournalPath == Journal).IsUndone);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UndoBlocksWholeGroupWhenOriginalNameIsOccupiedOrTargetChanged(bool occupied)
    {
        var source = Write("in/p.jpg"); Write("in/p.xmp", "tags");
        var service = Service(); var plan = Plan(service, source);
        await service.ExecuteAsync(plan, Journal, Commit, null, default);
        if (occupied) Write("in/p.jpg", "new original"); else File.WriteAllText(plan[0].Destination, "modified target");
        var results = await service.UndoAsync(Journal, Path.Combine(_root, "operations", "undo.jsonl"), Commit, null, default);
        Assert.All(results, result => Assert.False(result.Moved));
        Assert.True(File.Exists(Path.Combine(Out, "p.xmp"))); Assert.False(File.Exists(Path.Combine(_root, "in", "p.xmp")));
        Assert.Equal(occupied ? "new original" : "modified target", File.ReadAllText(occupied ? source : plan[0].Destination));
    }

    [Fact] public async Task InterruptedUndoRecoversThroughOriginalOperationWithoutMovingForwardAgain()
    {
        var source = Write("in/p.jpg"); var service = Service(); var plan = Plan(service, source);
        await service.ExecuteAsync(plan, Journal, Commit, null, default);
        var interrupted = Service(hook: (phase, _) => phase == "copy_verified" ? throw new MoveInterruptionException(phase) : Task.CompletedTask);
        await Assert.ThrowsAsync<MoveInterruptionException>(() => interrupted.UndoAsync(Journal,
            Path.Combine(_root, "operations", "undo.jsonl"), Commit, null, default));
        var results = await service.RecoverAsync(Journal, Commit, null, default);
        Assert.All(results, result => Assert.True(result.Moved, result.Error));
        Assert.True(File.Exists(source)); Assert.False(File.Exists(plan[0].Destination));
        Assert.True(service.ReadHistory(Path.GetDirectoryName(Journal)!).Single(item => item.JournalPath == Journal).IsUndone);
    }

    [Fact] public async Task TornLastJournalWriteIsPreservedAndRecoveredFromVerifiedPrefix()
    {
        var source = Write("in/p.jpg");
        var interrupted = Service(hook: (phase, _) => phase == "staged" ? throw new MoveInterruptionException(phase) : Task.CompletedTask);
        var plan = Plan(interrupted, source);
        await Assert.ThrowsAsync<MoveInterruptionException>(() => interrupted.ExecuteAsync(plan, Journal, Commit, null, default));
        await File.AppendAllTextAsync(Journal, "{\"Version\":2,\"Sequence\":");
        var result = Assert.Single(await Service().RecoverAsync(Journal, Commit, null, default));
        Assert.True(result.Moved, result.Error); Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Journal)!, "*.torn-*"));
    }

    [Fact] public async Task InterruptedUndoManifestPreparationReconcilesEveryInverseGroupExactlyOnce()
    {
        var sources = new[] { Write("in/first.jpg", "first"), Write("in/second.jpg", "second") };
        var service = Service(false);
        var plan = service.Plan(sources.Select(path => new MoveRequest(path, null)), Out, CollisionPolicy.Skip);
        Assert.All(await service.ExecuteAsync(plan, Journal, Commit, null, default), result => Assert.True(result.Moved, result.Error));
        var interrupted = Service(false, (phase, _) => phase == "undo_manifest_written"
            ? throw new MoveInterruptionException("first original pointer durable, all inverse groups durable") : Task.CompletedTask);
        await Assert.ThrowsAsync<MoveInterruptionException>(() => interrupted.UndoAsync(Journal,
            Path.Combine(_root, "operations", "undo.jsonl"), Commit, null, default));
        var callbacks = new List<string>();
        var results = await service.RecoverAsync(Journal, entry => { callbacks.Add(entry.Destination); return Commit(entry); }, null, default);
        Assert.Equal(2, results.Count); Assert.All(results, result => Assert.True(result.Moved, result.Error));
        Assert.Equal(sources.Order(), callbacks.Order());
        Assert.Equal("first", File.ReadAllText(sources[0])); Assert.Equal("second", File.ReadAllText(sources[1]));
        Assert.All(plan, entry => Assert.False(File.Exists(entry.Destination)));
        Assert.True(service.ReadHistory(Path.GetDirectoryName(Journal)!).Single(item => item.JournalPath == Journal).IsUndone);
        Assert.Equal(2, (await service.RecoverAsync(Journal, _ => throw new Exception("undo must not repeat"), null, default)).Count);
    }

    [Fact] public async Task SubstitutedInverseJournalIsRejectedBeforeMovingUnrelatedFiles()
    {
        var source = Write("in/p.jpg"); var service = Service(false);
        await service.ExecuteAsync(Plan(service, source), Journal, Commit, null, default);
        var undoJournal = Path.Combine(_root, "operations", "undo.jsonl");
        var interrupted = Service(false, (phase, _) => phase == "undo_manifest_written" ? throw new MoveInterruptionException(phase) : Task.CompletedTask);
        await Assert.ThrowsAsync<MoveInterruptionException>(() => interrupted.UndoAsync(Journal, undoJournal, Commit, null, default));
        var unrelated = Write("other/unrelated.jpg", "unrelated"); var unrelatedJournal = Path.Combine(_root, "operations", "other.jsonl");
        var other = Service(false, (phase, _) => phase == "planned" ? throw new MoveInterruptionException(phase) : Task.CompletedTask);
        await Assert.ThrowsAsync<MoveInterruptionException>(() => other.ExecuteAsync(Plan(other, unrelated), unrelatedJournal, Commit, null, default));
        File.Copy(unrelatedJournal, undoJournal, true);
        await Assert.ThrowsAsync<IOException>(() => service.RecoverAsync(Journal, _ => throw new Exception("no catalog writes"), null, default));
        Assert.Equal("unrelated", File.ReadAllText(unrelated)); Assert.False(File.Exists(source));
        Assert.Equal("unique original bytes", File.ReadAllText(Path.Combine(Out, "p.jpg")));
    }

    [Fact] public async Task UndoOfMoreThanOneManifestLimitUsesBoundedGroups()
    {
        var sources = Enumerable.Range(0, 257).Select(index => Write($"in/p{index}.jpg", $"photo {index}")).ToArray();
        var service = Service(false);
        var plan = service.Plan(sources.Select(path => new MoveRequest(path, null)), Out, CollisionPolicy.Skip);
        Assert.All(await service.ExecuteAsync(plan, Journal, Commit, null, default), result => Assert.True(result.Moved, result.Error));
        var results = await service.UndoAsync(Journal, Path.Combine(_root, "operations", "undo.jsonl"), Commit, null, default);
        Assert.Equal(sources.Length, results.Count); Assert.All(results, result => Assert.True(result.Moved, result.Error));
        for (var index = 0; index < sources.Length; index++) Assert.Equal($"photo {index}", File.ReadAllText(sources[index]));
    }

    [Fact] public async Task DamagedJournalStopsRecoveryWithoutTouchingAnyMedia()
    {
        var source = Write("in/p.jpg");
        var interrupted = Service(hook: (phase, _) => phase == "planned" ? throw new MoveInterruptionException(phase) : Task.CompletedTask);
        await Assert.ThrowsAsync<MoveInterruptionException>(() => interrupted.ExecuteAsync(Plan(interrupted, source), Journal, Commit, null, default));
        var bytes = await File.ReadAllTextAsync(Journal); await File.WriteAllTextAsync(Journal, bytes.Replace("planned", "tampered"));
        await Assert.ThrowsAsync<IOException>(() => Service().RecoverAsync(Journal, Commit, null, default));
        Assert.Equal("unique original bytes", File.ReadAllText(source));
        Assert.NotNull(Assert.Single(Service().ReadHistory(Path.GetDirectoryName(Journal)!)).Error);
    }

    [Fact] public async Task ExistingJournalIsNeverOverwritten()
    {
        var source = Write("in/p.jpg"); Write("operations/move.jsonl", "prior journal"); var service = Service();
        await Assert.ThrowsAsync<IOException>(() => service.ExecuteAsync(Plan(service, source), Journal, Commit, null, default));
        Assert.Equal("prior journal", File.ReadAllText(Journal)); Assert.True(File.Exists(source));
    }

    [Fact] public void DateLayoutAndCollisionRenameKeepAllCompanionNamesAligned()
    {
        var source = Write("in/p.jpg"); Write("in/p.xmp", "metadata"); Write("in/p.jpg.xmp", "specific metadata");
        Write("out/2026/09 Сентябрь/27/Дача/2026-09-27_12-34-56_p.jpg", "existing");
        var plan = Service().Plan([new(source, new DateTime(2026, 9, 27, 12, 34, 56))], Out, CollisionPolicy.Rename,
            layout: new(FolderLayout.YearMonthDay, FileNameStyle.DatePrefix, "Дача"));
        Assert.Equal(3, plan.Count); Assert.All(plan, entry => Assert.Null(entry.SkipReason));
        Assert.All(plan, entry => Assert.StartsWith("2026-09-27_12-34-56_p (1).", Path.GetFileName(entry.Destination)));
        Assert.All(plan, entry => Assert.Equal(Path.Combine(Out, "2026", "09 Сентябрь", "27", "Дача"), Path.GetDirectoryName(entry.Destination)));
    }

    [Fact] public void DateLayoutNeverInventsMissingCaptureDates()
    {
        var source = Write("in/p.jpg");
        var plan = Service().Plan([new(source, null)], Out, CollisionPolicy.Rename,
            layout: new(FolderLayout.YearMonthDay, FileNameStyle.DatePrefix, "Семья"));
        Assert.Equal(Path.Combine(Out, "Без даты съёмки", "Семья", "p.jpg"), Assert.Single(plan).Destination);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("LPT9.folder")]
    [InlineData("folder.")]
    [InlineData("a:b")]
    [InlineData("a\\b")]
    [InlineData("a?b")]
    public void InvalidEventNameIsRejectedBeforeAnyFileChanges(string name)
    {
        var source = Write("in/p.jpg");
        Assert.Throws<ArgumentException>(() => Service().Plan([new(source, null)], Out, CollisionPolicy.Skip, layout: new(EventName: name)));
        Assert.True(File.Exists(source)); Assert.False(Directory.Exists(Out));
    }

    [Fact] public async Task DatePrefixCanRenameWithinSameFolderAndUndoRestoresOriginalName()
    {
        var source = Write("in/p.jpg"); var service = Service(false);
        var plan = service.Plan([new(source, new DateTime(2026, 9, 27))], Path.GetDirectoryName(source)!, CollisionPolicy.Skip,
            layout: new(FileNameStyle: FileNameStyle.DatePrefix));
        Assert.Null(Assert.Single(plan).SkipReason);
        Assert.True(Assert.Single(await service.ExecuteAsync(plan, Journal, Commit, null, default)).Moved);
        Assert.True(Assert.Single(await service.UndoAsync(Journal, Path.Combine(_root, "operations", "undo.jsonl"), Commit, null, default)).Moved);
        Assert.Equal("unique original bytes", File.ReadAllText(source));
    }

    [Fact] public async Task RoundedDestinationTimestampDoesNotInvalidateVerifiedCopyAndUndoRestoresOriginalTimestamp()
    {
        var source = Write("in/p.jpg");
        File.SetLastWriteTimeUtc(source, new DateTime(2026, 9, 27, 1, 2, 3, DateTimeKind.Utc).AddTicks(1_234_560));
        var originalTime = File.GetLastWriteTimeUtc(source);
        var service = Service(hook: (phase, state) =>
        {
            if (phase == "copy_written") File.SetLastWriteTimeUtc(state.Temporary!, new DateTime(2026, 9, 27, 1, 2, 2, DateTimeKind.Utc));
            return Task.CompletedTask;
        });
        var plan = Plan(service, source);
        var moved = Assert.Single(await service.ExecuteAsync(plan, Journal, Commit, null, default));
        Assert.True(moved.Moved, moved.Error);
        var undo = Assert.Single(await Service().UndoAsync(Journal, Path.Combine(_root, "operations", "undo.jsonl"), Commit, null, default));
        Assert.True(undo.Moved, undo.Error); Assert.Equal(originalTime, File.GetLastWriteTimeUtc(source));
    }

    [Fact] public async Task FailedGroupPreparationNeverPublishesPartialRecoveryManifest()
    {
        var source = Write("in/p.jpg"); var xmp = Write("in/p.xmp", "tags"); var service = Service();
        var plan = Plan(service, source);
        await using var locked = new FileStream(xmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var results = await service.ExecuteAsync(plan, Journal, Commit, null, default);
        Assert.All(results, result => Assert.False(result.Moved));
        Assert.Empty(service.ReadPlan(Journal));
        Assert.True(File.Exists(source)); Assert.True(File.Exists(xmp));
        Assert.Empty(await service.RecoverAsync(Journal, Commit, null, default));
    }

    [Fact] public async Task DuplicateSourcePlanIsRejectedBeforeAnyFilesystemMutation()
    {
        var source = Write("in/p.jpg"); var service = Service(); var plan = Plan(service, source);
        var duplicate = plan[0] with { Destination = Path.Combine(Out, "second.jpg") };
        await Assert.ThrowsAsync<IOException>(() => service.ExecuteAsync([plan[0], duplicate], Journal, Commit, null, default));
        Assert.Equal("unique original bytes", File.ReadAllText(source));
    }

    [Theory]
    [InlineData(257, true)]
    [InlineData(50001, false)]
    public async Task OversizedPlanIsRejectedBeforeJournalCreationOrReadingSources(int count, bool oneGroup)
    {
        var plan = Enumerable.Range(0, count).Select(index => new MoveEntry(
            Path.Combine(_root, "missing", $"p{index}.jpg"), Path.Combine(Out, $"p{index}.jpg"), 1,
            DateTime.UnixEpoch, null, oneGroup ? "one-group" : $"group-{index}")).ToArray();
        var error = await Assert.ThrowsAsync<IOException>(() => Service().ExecuteAsync(plan, Journal, Commit, null, default));
        Assert.Contains("безопасный размер", error.Message);
        Assert.False(File.Exists(Journal)); Assert.False(Directory.Exists(Out));
    }

    [Fact] public async Task ReplacementDuringCleanupIsNeverDeletedByItsPath()
    {
        if (OperatingSystem.IsWindows()) return; // Windows equivalent explicitly tests deny-delete locking below.
        var source = Write("in/p.jpg");
        var relocated = source + ".external";
        var service = Service(hook: (phase, state) =>
        {
            if (phase == "source_locked_for_cleanup")
            {
                File.Move(state.Staging, relocated);
                File.WriteAllText(state.Staging, "external replacement");
            }
            return Task.CompletedTask;
        });
        var result = Assert.Single(await service.ExecuteAsync(Plan(service, source), Journal, Commit, null, default));
        Assert.False(result.Moved);
        Assert.Equal("unique original bytes", File.ReadAllText(relocated));
        Assert.Equal("unique original bytes", File.ReadAllText(Path.Combine(Out, "p.jpg")));
        Assert.Contains(Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Where(path => !path.Contains("operations")),
            path => File.ReadAllText(path) == "external replacement");
    }

    public void Dispose() => Directory.Delete(_root, true);
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows: native sharing, handle-bound disposition, NTFS streams and MoveFileEx must run on Windows CI.";
    }
}

public sealed class WindowsFileSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-native-test-" + Guid.NewGuid().ToString("N"));
    private string Journal => Path.Combine(_root, "move.jsonl");
    private string Source => Path.Combine(_root, "in", "p.jpg");
    private string Target => Path.Combine(_root, "out", "p.jpg");
    public WindowsFileSafetyTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        File.WriteAllText(Source, "photo content");
    }
    private IReadOnlyList<MoveEntry> Plan(FileMoveService service) => service.Plan([new(Source, null)], Path.GetDirectoryName(Target)!, CollisionPolicy.Skip);

    [WindowsFact] public async Task LockedCleanupHandlePreventsReplacingOrWritingVerifiedOriginal()
    {
        var reached = false;
        var service = new FileMoveService(new FileMoveOptions
        {
            AlwaysCopy = true,
            Checkpoint = (phase, state) =>
            {
                if (phase == "source_locked_for_cleanup")
                {
                    reached = true;
                    Assert.ThrowsAny<IOException>(() => File.Move(state.Staging, state.Staging + ".replaced"));
                    Assert.ThrowsAny<IOException>(() => File.WriteAllText(state.Staging, "replacement"));
                    Assert.ThrowsAny<IOException>(() => File.Delete(state.Staging));
                }
                return Task.CompletedTask;
            }
        });
        var result = Assert.Single(await service.ExecuteAsync(Plan(service), Journal, _ => Task.CompletedTask, null, default));
        Assert.True(result.Moved, result.Error); Assert.True(reached); Assert.False(File.Exists(Source));
        Assert.Equal("photo content", File.ReadAllText(Target));
        Assert.Empty(Directory.GetFiles(_root, "*.photoshelf-moving-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_root, "*.photoshelf-retained-*", SearchOption.AllDirectories));
    }

    [WindowsFact] public async Task SameVolumeUsesNativeRenameAndPreservesAlternateStreams()
    {
        File.WriteAllText(Source + ":PhotoShelfTest", "metadata bytes");
        var service = new FileMoveService(new FileMoveOptions
        {
            Checkpoint = (phase, _) => phase == "copying" ? throw new Exception("Same-volume move must not copy") : Task.CompletedTask
        });
        var result = Assert.Single(await service.ExecuteAsync(Plan(service), Journal, _ => Task.CompletedTask, null, default));
        Assert.True(result.Moved, result.Error); Assert.False(File.Exists(Source));
        Assert.Equal("photo content", File.ReadAllText(Target)); Assert.Equal("metadata bytes", File.ReadAllText(Target + ":PhotoShelfTest"));
    }

    [WindowsFact] public async Task CrossVolumeCopyRefusesToDiscardAlternateStreams()
    {
        File.WriteAllText(Source + ":PhotoShelfTest", "metadata bytes");
        var service = new FileMoveService(new FileMoveOptions { AlwaysCopy = true });
        var result = Assert.Single(await service.ExecuteAsync(Plan(service), Journal, _ => throw new Exception("must not commit"), null, default));
        Assert.False(result.Moved); Assert.Equal("photo content", File.ReadAllText(Source));
        Assert.Equal("metadata bytes", File.ReadAllText(Source + ":PhotoShelfTest")); Assert.False(File.Exists(Target));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
