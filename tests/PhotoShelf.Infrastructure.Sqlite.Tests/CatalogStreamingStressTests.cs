using PhotoShelf.Application.Catalog;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class CatalogStreamingStressTests
{
    [Fact] public async Task FiftyThousandItemsCanBeCancelledAfterFirst128WithoutLoadingRemainder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "photoshelf-stress-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteDesktopCatalogStore(directory); await store.InitializeAsync();
            var state = new LocalCatalogState { Items = Enumerable.Range(0, 50000).Select(i => new SavedMediaItem
                { Path = $"offline/{i:D6}.jpg", SizeBytes = 123, FileModifiedAt = new DateTime(2026, 1, 1) }).ToList() };
            await store.SaveAsync(state);
            // A preference change must preserve all catalog rows even with an empty Items list.
            await store.SaveAsync(new LocalCatalogState { DateGroupingMode = "CaptureDate" }, saveItems: false);
            using var cancellation = new CancellationTokenSource(); var received = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadBatchesAsync(batch =>
            {
                received += batch.Count; cancellation.Cancel(); return Task.CompletedTask;
            }, cancellation.Token));
            Assert.Equal(128, received);
            var loaded = await store.LoadAsync(); Assert.Equal(50000, loaded.Items.Count); Assert.Equal("CaptureDate", loaded.DateGroupingMode);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
