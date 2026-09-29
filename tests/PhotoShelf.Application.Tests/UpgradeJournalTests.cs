using System.Security.Cryptography;
using System.Text.Json;
using PhotoShelf.Application.Files;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class UpgradeJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "photoshelf-upgrade-journal-" + Guid.NewGuid().ToString("N"));
    private string Journal => Path.Combine(_root, "operations", "legacy.jsonl");
    public UpgradeJournalTests() => Directory.CreateDirectory(_root);
    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content); return path;
    }
    private (string Source, string Destination, string Staging, string Temporary) Legacy(string status = "completed", string? hash = null)
    {
        var source = Path.Combine(_root, "in", "image.jpg"); Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        var destination = Write("out/image.jpg", "synthetic original bytes");
        var staging = source + ".photoshelf-moving-" + Guid.NewGuid().ToString("N");
        var temporary = destination + ".photoshelf-copy-" + Guid.NewGuid().ToString("N");
        Write("operations/legacy.jsonl", JsonSerializer.Serialize(new
        {
            Source = source, Destination = destination, Staging = staging, Status = status,
            Hash = hash ?? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))), Error = (string?)null, Temporary = temporary
        }) + "\n");
        return (source, destination, staging, temporary);
    }

    [Fact] public async Task CompletedLegacyNeedsExplicitVerifiedReceiptAndDoesNotChangeEvidence()
    {
        var files = Legacy(); var journalBytes = File.ReadAllBytes(Journal); var mediaBytes = File.ReadAllBytes(files.Destination);
        var time = File.GetLastWriteTimeUtc(Journal);
        var service = new FileMoveService();
        Assert.NotNull(Assert.Single(service.ReadHistory(Path.GetDirectoryName(Journal)!)).Error);
        Assert.True((await LegacyJournalReviewService.ReviewAsync(Journal)).CanAcknowledge);
        Assert.NotNull(Assert.Single(service.ReadHistory(Path.GetDirectoryName(Journal)!)).Error);
        await LegacyJournalReviewService.AcknowledgeCompletedAsync(Journal);
        var history = Assert.Single(service.ReadHistory(Path.GetDirectoryName(Journal)!));
        Assert.True(history.IsLegacy); Assert.True(history.LegacyReviewed); Assert.Null(history.Error);
        Assert.Equal(0, history.PendingFiles); Assert.False(history.CanUndo);
        Assert.Equal(journalBytes, File.ReadAllBytes(Journal)); Assert.Equal(time, File.GetLastWriteTimeUtc(Journal));
        Assert.Equal(mediaBytes, File.ReadAllBytes(files.Destination)); Assert.False(File.Exists(files.Source));
        await Assert.ThrowsAsync<IOException>(() => service.RecoverAsync(Journal, _ => throw new Exception(), null, default));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("staging")]
    [InlineData("temporary")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("unfinished")]
    [InlineData("bad-hash")]
    [InlineData("offline-parent")]
    public async Task UncertainLegacyPreservesEverythingAndCannotBeAcknowledged(string condition)
    {
        var files = Legacy(condition == "unfinished" ? "catalog_committed" : "completed", condition == "bad-hash" ? "not-a-hash" : null);
        if (condition == "source") File.WriteAllText(files.Source, "new original");
        if (condition == "staging") File.WriteAllText(files.Staging, "retained original");
        if (condition == "temporary") File.WriteAllText(files.Temporary, "uncertain copy");
        if (condition == "missing") File.Delete(files.Destination);
        if (condition == "corrupt") File.WriteAllText(files.Destination, "changed externally");
        if (condition == "offline-parent") Directory.Delete(Path.GetDirectoryName(files.Source)!);
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        Assert.False((await LegacyJournalReviewService.ReviewAsync(Journal)).CanAcknowledge);
        await Assert.ThrowsAsync<IOException>(() => LegacyJournalReviewService.AcknowledgeCompletedAsync(Journal));
        Assert.Equal(before.Count, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact] public async Task ChangedJournalInvalidatesReceiptAndChangedMediaAfterPreviewCannotBeAcknowledged()
    {
        var files = Legacy();
        Assert.True((await LegacyJournalReviewService.ReviewAsync(Journal)).CanAcknowledge);
        File.WriteAllText(files.Destination, "changed since preview");
        await Assert.ThrowsAsync<IOException>(() => LegacyJournalReviewService.AcknowledgeCompletedAsync(Journal));
        File.WriteAllText(files.Destination, "synthetic original bytes");
        await LegacyJournalReviewService.AcknowledgeCompletedAsync(Journal);
        File.AppendAllText(Journal, File.ReadAllText(Journal));
        Assert.False(Assert.Single(new FileMoveService().ReadHistory(Path.GetDirectoryName(Journal)!)).LegacyReviewed);
    }

    [Theory]
    [InlineData("{\"Version\":3,\"Entry\":{}}\n")]
    [InlineData("{\"Source\":\"x\"}\n")]
    [InlineData("{\"Source\":\"x\"}")]
    public async Task UnknownOrTornFormatIsNeverInterpretedAsLegacy(string text)
    {
        Write("operations/legacy.jsonl", text);
        await Assert.ThrowsAnyAsync<IOException>(() => LegacyJournalReviewService.ReviewAsync(Journal));
        var history = Assert.Single(new FileMoveService().ReadHistory(Path.GetDirectoryName(Journal)!));
        Assert.False(history.IsLegacy); Assert.NotNull(history.Error);
        Assert.Equal(text, File.ReadAllText(Journal));
    }

    [Fact] public async Task CancellationDoesNotPublishReceipt()
    {
        Legacy(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LegacyJournalReviewService.AcknowledgeCompletedAsync(Journal, cancellation.Token));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Journal)!));
    }

    [Fact] public async Task InterruptedUndoUsesCopiedJournalsAndLeavesOldHistoryByteIdentical()
    {
        var source = Write("in/original.jpg", "original media");
        var old = Path.Combine(_root, "old", "operations"); var current = Path.Combine(_root, "generation", "operations");
        var forward = Path.Combine(old, "move.jsonl"); var undo = Path.Combine(old, "undo.jsonl");
        var service = new FileMoveService(new() { AlwaysCopy = true });
        var plan = service.Plan([new(source, null)], Path.Combine(_root, "target"), CollisionPolicy.Skip);
        await service.ExecuteAsync(plan, forward, _ => Task.CompletedTask, null, default);
        var interrupted = new FileMoveService(new() { AlwaysCopy = true, Checkpoint = (phase, _) => phase == "copy_verified" ? throw new MoveInterruptionException(phase) : Task.CompletedTask });
        await Assert.ThrowsAsync<MoveInterruptionException>(() => interrupted.UndoAsync(forward, undo, _ => Task.CompletedTask, null, default));
        var before = Directory.GetFiles(old).ToDictionary(path => path, File.ReadAllBytes);
        Directory.CreateDirectory(current);
        foreach (var path in before.Keys) File.Copy(path, Path.Combine(current, Path.GetFileName(path)));
        var migrated = new FileMoveService(new() { AlwaysCopy = true, JournalRelocation = new(old, current) });
        Assert.All(await migrated.RecoverAsync(forward, _ => Task.CompletedTask, null, default), result => Assert.True(result.Moved, result.Error));
        Assert.Equal("original media", File.ReadAllText(source)); Assert.False(File.Exists(plan[0].Destination));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.True(migrated.ReadHistory(current).Single(item => Path.GetFileName(item.JournalPath) == "move.jsonl").IsUndone);
        Assert.Throws<IOException>(() => new JournalPathRelocation(old, current).Resolve(Path.Combine(_root, "external.jsonl")));
    }

    [Fact] public async Task TornCurrentTailBlocksUndoAndRecoveryPreservesTailWithoutMovingCompletedFilesAgain()
    {
        var source = Write("in/original.jpg", "original media");
        var service = new FileMoveService(new() { AlwaysCopy = true });
        var plan = service.Plan([new(source, null)], Path.Combine(_root, "target"), CollisionPolicy.Skip);
        await service.ExecuteAsync(plan, Journal, _ => Task.CompletedTask, null, default);
        const string tail = "{\"Version\":2,\"Sequence\":";
        File.AppendAllText(Journal, tail);
        var history = Assert.Single(service.ReadHistory(Path.GetDirectoryName(Journal)!));
        Assert.NotNull(history.Error); Assert.True(history.HasRecoverableTail); Assert.False(history.CanUndo);
        Assert.All(await service.RecoverAsync(Journal, _ => throw new Exception("Do not repeat completed catalog commit"), null, default), result => Assert.True(result.Moved));
        Assert.Equal("original media", File.ReadAllText(plan[0].Destination)); Assert.False(File.Exists(source));
        Assert.Equal(tail, File.ReadAllText(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Journal)!, "*.torn-*"))));
        Assert.Null(Assert.Single(service.ReadHistory(Path.GetDirectoryName(Journal)!)).Error);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
