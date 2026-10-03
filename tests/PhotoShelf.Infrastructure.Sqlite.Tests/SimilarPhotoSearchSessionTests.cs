using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Duplicates;
using PhotoShelf.Infrastructure.Sqlite;
using Xunit;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class SimilarPhotoSearchSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "photoshelf-similar-session-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GroupsCloseFingerprintsAgainstStableReferenceAndDropsSingletons()
    {
        Directory.CreateDirectory(_root);
        await using var session = await SimilarPhotoSearchSession.CreateAsync(_root);
        var baseline = Fingerprint(0, 0);
        var close = Fingerprint(1UL << 18, 1UL << 7);
        var unrelated = Fingerprint(ulong.MaxValue, ulong.MaxValue);

        await session.AddAsync([
            Match("a", baseline),
            Match("b", close),
            Match("c", unrelated)]);
        await session.CompleteAsync(compareFolders: false);

        Assert.Equal(1, session.GroupCount);
        Assert.Equal(3, session.IndexedItemCount);
        var group = Assert.Single(await session.ReadGroupsAsync(0));
        Assert.Equal(2, group.TotalFiles);
        Assert.Equal(Path.Combine(_root, "a.png"), group.ReferencePath);
        Assert.Collection(group.Items,
            item => Assert.True(item.IsReference),
            item =>
            {
                Assert.False(item.IsReference);
                Assert.Equal(1, item.DifferenceDistance);
                Assert.Equal(1, item.AverageDistance);
            });
    }

    [Fact]
    public async Task DoesNotMergeDifferentAspectRatiosOrLongSimilarityChains()
    {
        Directory.CreateDirectory(_root);
        await using var session = await SimilarPhotoSearchSession.CreateAsync(_root);
        var a = new PerceptualFingerprint(1, 0, 0, 64, 32);
        var portrait = new PerceptualFingerprint(1, 1, 1, 32, 64);
        var chainEnd = new PerceptualFingerprint(1, 0b1111, 0b1111, 64, 32);

        await session.AddAsync([Match("a", a), Match("portrait", portrait), Match("chain", chainEnd)]);
        await session.CompleteAsync(compareFolders: false);

        Assert.Equal(0, session.GroupCount);
    }

    [Fact]
    public async Task CompareFoldersKeepsOnlyCrossFolderGroups()
    {
        Directory.CreateDirectory(_root);
        await using var session = await SimilarPhotoSearchSession.CreateAsync(_root);
        await session.AddAsync([
            Match("a", Fingerprint(0, 0), 1),
            Match("b", Fingerprint(1, 1), 2),
            Match("c", Fingerprint(0xF0, 0xF0), 1),
            Match("d", Fingerprint(0xF1, 0xF1), 1)]);
        await session.CompleteAsync(compareFolders: true);

        var group = Assert.Single(await session.ReadGroupsAsync(0));
        Assert.Equal(2, group.TotalFiles);
        Assert.Contains(group.Items, item => item.Item.Path.EndsWith("a.png", StringComparison.Ordinal));
        Assert.Contains(group.Items, item => item.Item.Path.EndsWith("b.png", StringComparison.Ordinal));
    }

    private SimilarPhotoMatch Match(string name, PerceptualFingerprint fingerprint, int mask = 0) => new(
        new SavedMediaItem
        {
            AssetId = name,
            Path = Path.Combine(_root, name + ".png"),
            SizeBytes = 100,
            FileModifiedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local),
            CaptureDate = new DateTime(2025, 12, 31),
            ObservationVersion = 1,
            Availability = FileAvailability.Available
        }, fingerprint, mask);

    private static PerceptualFingerprint Fingerprint(ulong difference, ulong average) =>
        new(1, difference, average, 64, 48);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
