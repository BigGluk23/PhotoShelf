using PhotoShelf.Application.Catalog;
using Xunit;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class DuplicateSearchSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-duplicate-session-test-" + Guid.NewGuid().ToString("N"));
    private static DuplicateSearchMatch Match(int group, int member, int mask = 0) => new(new SavedMediaItem
    {
        Path = Path.Combine(Path.GetTempPath(), $"photoshelf-synthetic-{group}-{member:D5}.jpg"),
        SizeBytes = group + 100, FileModifiedAt = new DateTime(2026, 1, 1), CaptureDate = new DateTime(2025, 2, 3)
    }, group.ToString("X64"), mask);

    [Fact]
    public async Task EveryGroupAndEveryMemberRemainReachableThroughBoundedPages()
    {
        await using var session = await DuplicateSearchSession.CreateAsync(_root);
        const int groups = DuplicateSearchSession.GroupsPerPage + 3;
        const int members = DuplicateSearchSession.MembersPerPage * 3 + 5;
        var batch = new List<DuplicateSearchMatch>();
        for (var group = 0; group < groups; group++)
            for (var member = 0; member < members; member++)
            {
                batch.Add(Match(group, member));
                if (batch.Count == 128) { await session.AddAsync(batch); batch.Clear(); }
            }
        await session.AddAsync(batch); await session.CompleteAsync(false);
        Assert.Equal(groups, session.GroupCount);
        var groupIds = new HashSet<long>();
        for (long page = 0; page < session.GroupCount; page += DuplicateSearchSession.GroupsPerPage)
        {
            var visible = await session.ReadGroupsAsync(page);
            Assert.InRange(visible.Count, 1, DuplicateSearchSession.GroupsPerPage);
            foreach (var group in visible)
            {
                Assert.True(groupIds.Add(group.Id));
                var paths = new HashSet<string> { group.KeeperPath };
                for (long offset = 0; offset < members - 1; offset += DuplicateSearchSession.MembersPerPage)
                {
                    var slice = await session.ReadGroupAsync(group.Id, offset);
                    Assert.InRange(slice.Items.Count, 1, DuplicateSearchSession.MembersPerPage + 1);
                    Assert.Equal(group.KeeperPath, slice.Items[0].Path);
                    Assert.All(slice.Items.Skip(1), item => Assert.True(paths.Add(item.Path)));
                }
                Assert.Equal(members, paths.Count);
            }
        }
        Assert.Equal(groups, groupIds.Count);
    }

    [Fact]
    public async Task CompareFoldersDoesNotInventKeeperFromOutsideScopedSnapshot()
    {
        await using var session = await DuplicateSearchSession.CreateAsync(_root);
        await session.AddAsync(new[] { Match(1, 1, 1), Match(1, 2, 1), Match(2, 1, 1), Match(2, 2, 2), Match(3, 1, 3) });
        await session.CompleteAsync(true);
        var group = Assert.Single(await session.ReadGroupsAsync(0));
        Assert.Equal(Match(2, 1).Hash, group.Hash);
        await Assert.ThrowsAsync<IOException>(() => session.SetKeeperAsync(group.Id, Match(1, 1).Item.Path));
        await session.SetKeeperAsync(group.Id, Match(2, 2).Item.Path);
        var refreshed = await session.ReadGroupAsync(group.Id, 0);
        Assert.Equal(Match(2, 2).Item.Path, refreshed.KeeperPath);
        Assert.True(await session.ContainsKeeperAsync(new[] { refreshed.KeeperPath.ToUpperInvariant() }));
        // A companion or stale UI choice cannot remove the persisted surviving copy.
        await session.MarkMovedAsync(new[] { refreshed.KeeperPath, Match(2, 1).Item.Path });
        refreshed = await session.ReadGroupAsync(group.Id, 0);
        Assert.Single(refreshed.Items); Assert.Equal(1, refreshed.TotalFiles);
    }

    [Fact]
    public async Task CancellationNeverPublishesPartialGroupsAndDisposalRemovesOnlyOwnedScratch()
    {
        Directory.CreateDirectory(_root); var sentinel = Path.Combine(_root, "do-not-touch.txt"); await File.WriteAllTextAsync(sentinel, "original");
        await using (var session = await DuplicateSearchSession.CreateAsync(_root))
        {
            await session.AddAsync(new[] { Match(1, 1), Match(1, 2) });
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CompleteAsync(false, cancel.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadGroupsAsync(0));
            await session.CompleteAsync(false);
            Assert.Single(await session.ReadGroupsAsync(0));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadGroupsAsync(0, cancel.Token));
        }
        Assert.Equal("original", await File.ReadAllTextAsync(sentinel));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "duplicate-searches")));
    }

    [Fact]
    public async Task CachedHashLetterCaseDoesNotSplitIdenticalFiles()
    {
        await using var session = await DuplicateSearchSession.CreateAsync(_root);
        var first = Match(15, 1); var second = Match(15, 2);
        await session.AddAsync(new[] { first, second with { Hash = second.Hash.ToLowerInvariant() } });
        await session.CompleteAsync(false);
        var group = Assert.Single(await session.ReadGroupsAsync(0));
        Assert.Equal(2, group.TotalFiles); Assert.Equal(first.Hash, group.Hash);
    }

    [Fact]
    public async Task CandidateBatchCannotGrowWithoutBound()
    {
        await using var session = await DuplicateSearchSession.CreateAsync(_root);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.AddAsync(Enumerable.Range(0, 129).Select(i => Match(1, i)).ToArray()));
        await session.CompleteAsync(false); Assert.Equal(0, session.GroupCount);
    }

    [Fact]
    public async Task SelectionPersistsAcrossPagesAndChangingKeeperPreservesACompleteDecision()
    {
        await using var session = await DuplicateSearchSession.CreateAsync(_root);
        var matches = Enumerable.Range(0, DuplicateSearchSession.MembersPerPage + 7)
            .Select(member => Match(21, member)).ToArray();
        await session.AddAsync(matches.Take(64).ToArray());
        await session.AddAsync(matches.Skip(64).ToArray());
        await session.CompleteAsync(false);

        await session.SetAllExtrasSelectedAsync(true);
        var group = Assert.Single(await session.ReadGroupsAsync(0));
        Assert.Equal(matches.Length - 1, (await session.ReadSelectionSummaryAsync()).SelectedFiles);

        var newKeeper = matches[^1].Item.Path;
        var oldKeeper = group.KeeperPath;
        await session.SetKeeperAsync(group.Id, newKeeper);
        var selection = await session.ReadQuarantineSelectionAsync();

        Assert.Equal(matches.Length - 1, selection.Count);
        Assert.DoesNotContain(selection, item => item.Item.Path.Equals(newKeeper, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(selection, item => item.Item.Path.Equals(oldKeeper, StringComparison.OrdinalIgnoreCase));
        Assert.All(selection, item => Assert.Equal(newKeeper, item.KeeperPath));
        await Assert.ThrowsAsync<IOException>(() => session.SetItemSelectedAsync(newKeeper, true));
        await Assert.ThrowsAsync<IOException>(() => session.ReadQuarantineSelectionAsync(2));

        await session.SetItemSelectedAsync(oldKeeper, false);
        Assert.Equal(matches.Length - 2, (await session.ReadSelectionSummaryAsync()).SelectedFiles);
        var finalPage = await session.ReadGroupAsync(group.Id, DuplicateSearchSession.MembersPerPage);
        Assert.DoesNotContain(oldKeeper, finalPage.SelectedPaths);
    }

    [Fact]
    public async Task PriorityFolderRuleAndBulkMarkApplyToTheWholeSnapshot()
    {
        await using var session = await DuplicateSearchSession.CreateAsync(_root);
        var preferred = Path.Combine(_root, "preferred");
        var archive = Path.Combine(_root, "archive", "copies");
        var matches = new List<DuplicateSearchMatch>();
        for (var group = 30; group < 30 + DuplicateSearchSession.GroupsPerPage + 4; group++)
        {
            var hash = group.ToString("X64");
            matches.Add(new DuplicateSearchMatch(new SavedMediaItem
            {
                Path = Path.Combine(archive, $"photo-{group}.jpg"), SizeBytes = group + 100,
                FileModifiedAt = new DateTime(2026, 2, 2)
            }, hash));
            matches.Add(new DuplicateSearchMatch(new SavedMediaItem
            {
                Path = Path.Combine(preferred, $"photo-{group}.jpg"), SizeBytes = group + 100,
                FileModifiedAt = new DateTime(2025, 1, 1)
            }, hash));
        }
        foreach (var batch in matches.Chunk(128)) await session.AddAsync(batch);
        await session.CompleteAsync(false);

        await session.ApplyKeeperRuleAsync(DuplicateKeeperRule.PriorityFolder, preferred);
        await session.SetAllExtrasSelectedAsync(true);
        var summary = await session.ReadSelectionSummaryAsync();

        Assert.Equal(DuplicateSearchSession.GroupsPerPage + 4, summary.SelectedGroups);
        Assert.Equal(DuplicateSearchSession.GroupsPerPage + 4, summary.SelectedFiles);
        for (long offset = 0; offset < session.GroupCount; offset += DuplicateSearchSession.GroupsPerPage)
            Assert.All(await session.ReadGroupsAsync(offset), group =>
                Assert.StartsWith(preferred, group.KeeperPath, StringComparison.OrdinalIgnoreCase));
        Assert.All(await session.ReadQuarantineSelectionAsync(), item =>
            Assert.StartsWith(archive, item.Item.Path, StringComparison.OrdinalIgnoreCase));

        // Changing a mass rule after "mark all" keeps one survivor and every other exact
        // copy selected instead of silently losing one decision per group.
        await session.ApplyKeeperRuleAsync(DuplicateKeeperRule.NewestFile);
        summary = await session.ReadSelectionSummaryAsync();
        Assert.Equal(DuplicateSearchSession.GroupsPerPage + 4, summary.SelectedFiles);
        for (long offset = 0; offset < session.GroupCount; offset += DuplicateSearchSession.GroupsPerPage)
            Assert.All(await session.ReadGroupsAsync(offset), group =>
                Assert.StartsWith(archive, group.KeeperPath, StringComparison.OrdinalIgnoreCase));
        Assert.All(await session.ReadQuarantineSelectionAsync(), item =>
            Assert.StartsWith(preferred, item.Item.Path, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
