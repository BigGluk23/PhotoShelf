using PhotoShelf.Application.Catalog;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class DesktopCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-db-" + Guid.NewGuid().ToString("N"));
    [Fact] public async Task SettingsAndStreamingCatalogRoundTripWithoutReadingMediaFiles()
    {
        var store = new SqliteDesktopCatalogStore(_root); await store.InitializeAsync();
        var state = new LocalCatalogState { BackgroundLoadMode = "Quiet", DateGroupingMode = "CaptureDate", TileWidth = 173.5, IncludeSystemFolders = true,
            ActiveFolder = "D:\\Фото", ViewMode = "Folder", SortNewestFirst = false, IncludeSubfolders = false,
            ExcludedFolders = new() { "D:\\Private" }, ExpandedFolders = new() { "D:\\Фото" },
            Items = Enumerable.Range(0, 300).Select(i => new SavedMediaItem { Path = $"D:\\Фото\\{i}.jpg", SizeBytes = 42, FileModifiedAt = new DateTime(2026, 1, 1), IsFavorite = i == 7 }).ToList() };
        await store.SaveAsync(state);
        var settings = await store.LoadAsync(includeItems: false);
        Assert.Equal("Quiet", settings.BackgroundLoadMode);
        Assert.Empty(settings.Items); Assert.True(settings.ReadItemsFromSqlite); Assert.Equal("CaptureDate", settings.DateGroupingMode);
        Assert.True(settings.IncludeSystemFolders); Assert.Equal(173.5, settings.TileWidth); Assert.Equal(state.ActiveFolder, settings.ActiveFolder);
        Assert.False(settings.IncludeSubfolders); Assert.False(settings.SortNewestFirst); Assert.Equal(state.ExpandedFolders, settings.ExpandedFolders);
        var sizes = new List<int>(); var all = new List<SavedMediaItem>();
        await store.ReadBatchesAsync(batch => { sizes.Add(batch.Count); all.AddRange(batch); return Task.CompletedTask; }, default);
        Assert.Equal(new[] { 128, 128, 44 }, sizes); Assert.Equal(300, all.Count); Assert.Single(all, x => x.IsFavorite); Assert.All(all, x => Assert.Equal(42, x.SizeBytes));
    }
    [Fact] public async Task MoveUpdatesCatalogMetadataAndHashInOneTransaction()
    {
        var store = new SqliteDesktopCatalogStore(_root); var metadata = new MetadataIndexStore(_root); var hashes = new DuplicateHashStore(_root);
        await store.InitializeAsync(); await metadata.InitializeAsync(); await hashes.InitializeAsync();
        var date = new DateTime(2026,9,1);
        await store.SaveAsync(new LocalCatalogState { Items = new() { new SavedMediaItem { Path = "old.jpg", SizeBytes = 42, IsFavorite = true } } });
        await metadata.SaveAsync("old.jpg", 42, date, date);
        await hashes.SaveAsync(new[] { new SavedDuplicateHash { Path = "old.jpg", SizeBytes = 42, Hash = "ABC", FileModifiedAt = date } });
        await store.MoveItemAsync("old.jpg", "new.jpg");
        Assert.Equal("new.jpg", Assert.Single((await store.LoadAsync()).Items).Path);
        Assert.True((await metadata.LoadCaptureDatesAsync()).ContainsKey("new.jpg"));
        Assert.Equal("new.jpg", Assert.Single(await hashes.LoadAsync()).Path);
        await store.MoveItemAsync("new.jpg", "quarantine.jpg", true);
        Assert.Empty((await store.LoadAsync()).Items); Assert.Empty(await hashes.LoadAsync()); Assert.Empty(await metadata.LoadCaptureDatesAsync());
    }
    [Fact] public async Task EmptySavedCatalogRemainsAuthoritative()
    {
        var store = new SqliteDesktopCatalogStore(_root); await store.InitializeAsync(); await store.SaveAsync(new LocalCatalogState());
        Assert.True((await store.LoadAsync(includeItems: false)).ReadItemsFromSqlite);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
