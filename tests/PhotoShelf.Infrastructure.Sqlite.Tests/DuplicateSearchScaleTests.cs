using System.Diagnostics;
using PhotoShelf.Application.Catalog;
using Xunit;
using Xunit.Abstractions;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class DuplicateSearchScaleTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "CatalogScale")]
    public async Task HundredThousandCandidatesStayOnDiskAndAllHugeGroupPagesRemainReachable()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoshelf-duplicate-scale-" + Guid.NewGuid().ToString("N"));
        const int total = 100_000, largeGroupSize = 50_000;
        var baseline = GC.GetTotalMemory(true); long peak = baseline;
        var timer = Stopwatch.StartNew();
        try
        {
            await using var session = await DuplicateSearchSession.CreateAsync(root);
            var batch = new List<DuplicateSearchMatch>(128);
            for (var i = 0; i < total; i++)
            {
                var group = i < largeGroupSize ? 0 : 1 + (i - largeGroupSize) / 2;
                batch.Add(new(new SavedMediaItem { Path = Path.Combine(root, $"synthetic-{i:D8}.jpg"), SizeBytes = 100 + group }, group.ToString("X64")));
                if (batch.Count == 128) { await session.AddAsync(batch); batch.Clear(); peak = Math.Max(peak, GC.GetTotalMemory(false)); }
            }
            if (batch.Count > 0) await session.AddAsync(batch);
            output.WriteLine($"100k snapshot writes: {timer.Elapsed.TotalMilliseconds:F1} ms");
            using (var cancellation = new CancellationTokenSource())
            {
                // A large aggregate allows the native sqlite3_interrupt registration to be exercised.
                var cancelled = session.CompleteAsync(false, cancellation.Token);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            }
            timer.Restart(); await session.CompleteAsync(false);
            output.WriteLine($"100k SQL grouping: {timer.Elapsed.TotalMilliseconds:F1} ms; groups={session.GroupCount}");
            Assert.Equal(25_001, session.GroupCount);
            long visited = 0; DuplicateGroupPage? large = null;
            timer.Restart();
            for (long offset = 0; offset < session.GroupCount; offset += DuplicateSearchSession.GroupsPerPage)
            {
                var page = await session.ReadGroupsAsync(offset);
                Assert.InRange(page.Count, 1, DuplicateSearchSession.GroupsPerPage);
                Assert.All(page, group => Assert.InRange(group.Items.Count, 1, DuplicateSearchSession.MembersPerPage + 1));
                visited += page.Count; large ??= page.FirstOrDefault(group => group.TotalFiles == largeGroupSize);
                peak = Math.Max(peak, GC.GetTotalMemory(false));
            }
            Assert.Equal(session.GroupCount, visited); Assert.NotNull(large);
            output.WriteLine($"All {visited} groups paged: {timer.Elapsed.TotalMilliseconds:F1} ms");
            long reached = 1; string? last = null;
            timer.Restart();
            for (long offset = 0; offset < largeGroupSize - 1; offset += DuplicateSearchSession.MembersPerPage)
            {
                var page = await session.ReadGroupAsync(large!.Id, offset);
                foreach (var item in page.Items.Skip(1))
                {
                    if (last is not null) Assert.True(StringComparer.OrdinalIgnoreCase.Compare(last, item.Path) < 0);
                    last = item.Path; reached++;
                }
                peak = Math.Max(peak, GC.GetTotalMemory(false));
            }
            Assert.Equal(largeGroupSize, reached);
            output.WriteLine($"All 50k same-hash members paged: {timer.Elapsed.TotalMilliseconds:F1} ms");
            output.WriteLine($"Sampled managed heap baseline={baseline / 1048576d:F2} MiB, peak={peak / 1048576d:F2} MiB; process-wide sampled metric, not UI/RSS limit.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
