using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotoShelf.Application.Files;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class DurableJournalBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "photoshelf-journal-stream-test-" + Guid.NewGuid().ToString("N"));
    private sealed record LegacyRecord(int Version, long Sequence, string PreviousChecksum, MoveJournalEntry Entry, string Checksum);
    private MoveJournalEntry Entry(string name = "a")
    {
        var source = Path.Combine(_root, name + ".jpg"); var destination = Path.Combine(_root, "out", name + ".jpg");
        return new(source, destination, source + ".photoshelf-moving-test", "completed", new string('A', 64), null,
            destination + ".photoshelf-copy-test", 10, new DateTime(2026, 1, 1), "group");
    }
    private async Task<string> WriteAsync(int records, MoveJournalEntry entry)
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, "operation.jsonl");
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false)); string previous = "";
        for (var sequence = 1; sequence <= records; sequence++)
        {
            var record = new LegacyRecord(2, sequence, previous, entry, "");
            previous = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record))));
            await writer.WriteLineAsync(JsonSerializer.Serialize(record with { Checksum = previous }));
        }
        return path;
    }

    [Fact]
    public async Task LargeValidV2HistoryReducesToLatestStateWithoutDroppingManifestMembers()
    {
        var manifest = Entry() with { Status = "group_manifest", Members = new[] { Entry(), Entry("b") } };
        var path = await WriteAsync(5000, manifest);
        Assert.True(new FileInfo(path).Length > 4 * 1024 * 1024);
        var plan = new FileMoveService().ReadPlan(path);
        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, item => item.Source == Entry().Source);
        Assert.Contains(plan, item => item.Source == Entry("b").Source);
        Assert.DoesNotContain("WindowsSecurityDescriptor", await File.ReadAllTextAsync(path)); // legacy wire representation remains unchanged
    }

    [Fact]
    public async Task TornTailIsPreservedExactlyBeforeRecoveryAppends()
    {
        var path = await WriteAsync(2, Entry());
        const string tail = "{\"Version\":2,\"unfinished\":";
        await File.AppendAllTextAsync(path, tail);
        var length = new FileInfo(path).Length;
        Assert.Single(new FileMoveService().ReadPlan(path));
        Assert.Equal(length, new FileInfo(path).Length); // read-only validation does not repair/truncate
        var results = await new FileMoveService().RecoverAsync(path, _ => throw new Exception("Already completed; catalog callback is unexpected"), null, CancellationToken.None);
        Assert.True(Assert.Single(results).Moved);
        Assert.Equal(tail, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_root, "*.torn-*"))));
        Assert.Equal(length - Encoding.UTF8.GetByteCount(tail), new FileInfo(path).Length);
    }

    [Fact]
    public async Task OversizedLineFailsClosedWithoutAlteringJournalOrSyntheticOriginal()
    {
        Directory.CreateDirectory(_root); var source = Path.Combine(_root, "original.jpg"); await File.WriteAllTextAsync(source, "synthetic original");
        var path = Path.Combine(_root, "oversized.jsonl");
        await using (var stream = new FileStream(path, FileMode.CreateNew))
        {
            var block = Enumerable.Repeat((byte)'x', 65536).ToArray();
            for (var i = 0; i < 65; i++) await stream.WriteAsync(block);
        }
        var length = new FileInfo(path).Length;
        var error = Assert.Throws<IOException>(() => new FileMoveService().ReadPlan(path));
        Assert.Contains("Не обрезайте журнал", error.Message);
        Assert.Equal(length, new FileInfo(path).Length); Assert.Empty(Directory.GetFiles(_root, "*.torn-*"));
        Assert.Equal("synthetic original", await File.ReadAllTextAsync(source));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
