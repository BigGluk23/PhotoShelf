using System.Diagnostics;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Infrastructure.Sqlite;
using Xunit.Abstractions;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

/// <summary>Real SQLite throughput diagnostics, not a WPF/UI latency benchmark.</summary>
public sealed class CatalogScaleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    [Trait("Category","CatalogScale")]
    public async Task LargeCatalogReturnsBoundedPagesWithoutMaterializingAllItems(int count)
    {
        var root=Path.Combine(Path.GetTempPath(),"photoshelf-scale-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new SqliteDesktopCatalogStore(root);await store.InitializeAsync();
            var watch=Stopwatch.StartNew();
            await store.UpsertItemsAsync(Enumerable.Range(0,count).Select(i=>new SavedMediaItem
            {
                Path=$"D:\\Фото\\{2000+i%27}\\{i:D8}.jpg",SizeBytes=1000+i%100,
                FileModifiedAt=new DateTime(2026,1+i%12,1+i%27,12,0,0,DateTimeKind.Local),
                CaptureDate=i%7==0 ? null : new DateTime(2000+i%27,1+i%12,1),MetadataIndexed=true
            }));
            var ingestMs=watch.ElapsedMilliseconds;var query=new CatalogViewQuery{PageSize=128};
            watch.Restart();var first=await store.QueryPageAsync(query);var pageMs=watch.Elapsed.TotalMilliseconds;
            watch.Restart();var groups=await store.QueryGroupsAsync(query);var groupMs=watch.Elapsed.TotalMilliseconds;
            Assert.Equal(count,groups.Sum(x=>x.Count));Assert.Equal(128,first.Items.Count);Assert.True(first.HasMore);
            watch.Restart();var distant=await store.QueryPageAsync(query,count-128,128);var offsetMs=watch.Elapsed.TotalMilliseconds;
            Assert.Equal(128,distant.Items.Count);Assert.False(distant.HasMore);
            watch.Restart();var capture=await store.QueryPageAsync(query with{UseCaptureDate=true});var captureMs=watch.Elapsed.TotalMilliseconds;
            Assert.Equal(128,capture.Items.Count);Assert.NotNull(capture.Items[0].CaptureDate);
            using var cancellation=new CancellationTokenSource();var received=0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>
            {
                await foreach(var item in store.EnumerateAsync(query,cancellation.Token))
                { received++;if(received==128)cancellation.Cancel(); }
            });
            Assert.Equal(128,received);
            output.WriteLine($"SQLITE_BENCH count={count} ingest_ms={ingestMs} first128_ms={pageMs:F2} groups_ms={groupMs:F2} offset128_ms={offsetMs:F2} capture128_ms={captureMs:F2}");
        }
        finally {SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
