using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotoShelf.Application.Files;
using Xunit;
using Xunit.Abstractions;

namespace PhotoShelf.Application.Tests;

public sealed class DurableJournalScaleTests(ITestOutputHelper output)
{
    private sealed record JournalRecord(int Version, long Sequence, string PreviousChecksum, MoveJournalEntry Entry, string Checksum);

    [Fact, Trait("Category", "CatalogScale")]
    public async Task HundredThousandHistoricalStatesValidateToOneLatestEntry()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "photoshelf-journal-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "history.jsonl"); var source = Path.Combine(root, "synthetic.jpg"); var target = Path.Combine(root, "out.jpg");
            var entry = new MoveJournalEntry(source, target, source + ".photoshelf-moving-test", "completed", new string('A', 64), null,
                target + ".photoshelf-copy-test", 10, new DateTime(2026, 1, 1), "group");
            await using (var writer = new StreamWriter(path, false, new UTF8Encoding(false), 65536))
            {
                string previous = "";
                for (var i = 1; i <= 100_000; i++)
                {
                    var record = new JournalRecord(2, i, previous, entry, "");
                    previous = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record))));
                    await writer.WriteLineAsync(JsonSerializer.Serialize(record with { Checksum = previous }));
                }
            }
            var baseline = GC.GetTotalMemory(true); long peak = baseline;
            var timer = Stopwatch.StartNew();
            var reading = Task.Run(() => new FileMoveService().ReadPlan(path));
            while (!reading.IsCompleted) { peak = Math.Max(peak, GC.GetTotalMemory(false)); await Task.Delay(10); }
            var plan = await reading;
            Assert.Equal(source, Assert.Single(plan).Source);
            output.WriteLine($"100k journal records ({new FileInfo(path).Length / 1048576d:F1} MiB), latest states={plan.Count}, validation={timer.Elapsed.TotalMilliseconds:F1} ms");
            output.WriteLine($"Sampled managed heap baseline={baseline / 1048576d:F2} MiB, peak={peak / 1048576d:F2} MiB; includes transient JSON parsing/test process, not RSS/UI metric.");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => new FileMoveService().ReadPlan(path, cancel.Token));
        }
        finally { Directory.Delete(root, true); }
    }
}
