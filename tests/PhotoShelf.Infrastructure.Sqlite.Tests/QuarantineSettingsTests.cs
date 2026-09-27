using PhotoShelf.Application.Catalog;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class QuarantineSettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("photoshelf-quarantine-settings-").FullName;

    [Fact]
    public async Task ChangingChosenRootAndSavingViewDoesNotLosePreviousQuarantineOrUndoState()
    {
        var store = new SqliteDesktopCatalogStore(_root); await store.InitializeAsync();
        var first = Path.Combine(_root, "first"); var second = Path.Combine(_root, "second");
        var batch = Path.Combine(first, "batch-one");
        var original = Path.Combine(_root, "photo.jpg"); var quarantined = Path.Combine(batch, "photo.jpg");
        await store.UpsertItemsAsync(new[] { new SavedMediaItem { Path = original, SizeBytes = 12, IsFavorite = true } });
        await store.SetQuarantineDirectoryAsync(first);
        await store.RegisterQuarantineBatchAsync(batch);
        await store.MoveItemAsync(original, quarantined, removeFromLibrary: true);
        await store.SetQuarantineDirectoryAsync(second);
        await store.SaveAsync(new LocalCatalogState { ShowVideos = false }, saveItems: false);
        var state = await store.LoadAsync();
        Assert.Equal(second, state.QuarantineDirectory);
        Assert.Equal(new[] { batch }, state.QuarantineBatchDirectories);
        Assert.Empty(state.Items);
        // Recovery classifies an old destination by the saved batch, not by the currently selected root.
        await store.MoveItemAsync(original, quarantined, removeFromLibrary: state.QuarantineBatchDirectories.Any(root => StoragePrivacyPolicy.IsUnder(quarantined, root)));
        Assert.Empty((await store.LoadAsync()).Items);
        await store.MoveItemAsync(quarantined, original, removeFromLibrary: state.QuarantineBatchDirectories.Any(root => StoragePrivacyPolicy.IsUnder(original, root)));
        var restored = Assert.Single((await store.LoadAsync()).Items);
        Assert.Equal(original, restored.Path); Assert.True(restored.IsFavorite);
    }

    [Fact]
    public async Task BatchRegistrationIsAppendOnlyAndIdempotent()
    {
        var store = new SqliteDesktopCatalogStore(_root); await store.InitializeAsync();
        var a = Path.Combine(_root, "a"); var b = Path.Combine(_root, "b");
        await store.RegisterQuarantineBatchAsync(a); await store.RegisterQuarantineBatchAsync(b); await store.RegisterQuarantineBatchAsync(a);
        Assert.Equal(new[] { a, b }, (await store.LoadAsync(includeItems: false)).QuarantineBatchDirectories);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetQuarantineDirectoryAsync("relative/path"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
