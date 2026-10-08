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
        // CI uses its explicit scratch disk to separate runner storage from system
        // profile TEMP latency. The same real
        // writer, FULL/WAL, transaction size and row counts are exercised on either path.
        var root=Path.Combine(Environment.GetEnvironmentVariable("PHOTOSHELF_SCALE_DATA_ROOT") ?? Path.GetTempPath(),
            "photoshelf-scale-"+Guid.NewGuid().ToString("N"));
        var diagnostics = Environment.GetEnvironmentVariable("PHOTOSHELF_SCALE_DIAGNOSTICS");
        void Stage(string phase)
        {
            var line = $"{DateTime.UtcNow:O} count={count} {phase}";
            output.WriteLine(line);
            if (string.IsNullOrWhiteSpace(diagnostics)) return;
            Directory.CreateDirectory(diagnostics);
            File.AppendAllText(Path.Combine(diagnostics, $"catalog-{count}.progress.log"), line + Environment.NewLine);
        }
        IEnumerable<SavedMediaItem> Items()
        {
            for (var i = 0; i < count; i++)
            {
                if (i % 10_000 == 0) Stage($"upsert-enumerated={i}");
                yield return new SavedMediaItem
                {
                    Path=$"D:\\Фото\\{2000+i%27}\\{i:D8}.jpg",SizeBytes=1000+i%100,
                    FileModifiedAt=new DateTime(2026,1+i%12,1+i%27,12,0,0,DateTimeKind.Local),
                    CaptureDate=i%7==0 ? null : new DateTime(2000+i%27,1+i%12,1),MetadataIndexed=true
                };
            }
        }
        try
        {
            Stage($"initialize-start volume={Path.GetPathRoot(root)}");
            var store=new SqliteDesktopCatalogStore(root);await store.InitializeAsync();
            Stage("seed-start");
            var watch=Stopwatch.StartNew();
            if (count == 1_000_000) await SeedMillionAsync(root);
            else await store.UpsertItemsAsync(Items());
            Stage("seed-complete; first-page-start");
            var ingestMs=watch.ElapsedMilliseconds;var query=new CatalogViewQuery{PageSize=128};
            watch.Restart();var first=await store.QueryPageAsync(query);var pageMs=watch.Elapsed.TotalMilliseconds;
            Stage("groups-start");
            watch.Restart();var groups=await store.QueryGroupsAsync(query);var groupMs=watch.Elapsed.TotalMilliseconds;
            Assert.Equal(count,groups.Sum(x=>x.Count));Assert.Equal(128,first.Items.Count);Assert.True(first.HasMore);
            Stage("distant-page-start");
            watch.Restart();var distant=await store.QueryPageAsync(query,count-128,128);var offsetMs=watch.Elapsed.TotalMilliseconds;
            Assert.Equal(128,distant.Items.Count);Assert.False(distant.HasMore);
            Stage("capture-page-start");
            watch.Restart();var capture=await store.QueryPageAsync(query with{UseCaptureDate=true});var captureMs=watch.Elapsed.TotalMilliseconds;
            Assert.Equal(128,capture.Items.Count);Assert.NotNull(capture.Items[0].CaptureDate);
            Stage("cancellation-start");
            using var cancellation=new CancellationTokenSource();var received=0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>
            {
                await foreach(var item in store.EnumerateAsync(query,cancellation.Token))
                { received++;if(received==128)cancellation.Cancel(); }
            });
            Assert.Equal(128,received);
            Stage("bounded-update-start");
            // Exercise the real bounded writer at the full catalog size too. Bulk
            // fixture setup above is not evidence of production ingestion throughput.
            var ids = first.Items.ToDictionary(item => item.Path, item => item.AssetId);
            foreach (var item in first.Items) item.IsFavorite = true;
            await store.UpsertItemsAsync(first.Items, preserveFavorites: false);
            var saved = await store.GetItemsByPathsAsync(first.Items.Select(item => item.Path).ToArray());
            Assert.Equal(128, saved.Count);
            Assert.All(saved.Values, item => { Assert.True(item.IsFavorite); Assert.Equal(ids[item.Path], item.AssetId); });
            Assert.Equal(count, await store.CountAsync(query));
            Stage("complete");
            output.WriteLine($"SQLITE_BENCH count={count} setup_ms={ingestMs} setup={(count == 1_000_000 ? "fixture-only-64MiB" : "production-upsert")} first128_ms={pageMs:F2} groups_ms={groupMs:F2} offset128_ms={offsetMs:F2} capture128_ms={captureMs:F2}");
        }
        finally {SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    private static async Task SeedMillionAsync(string root)
    {
        // Same fixture-only connection profile as PerformanceRunner. Close it before
        // exercising production queries/writes; preserve real schema/indexes/triggers,
        // FULL durability and WAL. No media files are created or opened by this seed.
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, "catalog-v2.sqlite"), Pooling = false }.ToString());
        await db.OpenAsync();
        await using var setup = db.CreateCommand();
        setup.CommandText = "PRAGMA synchronous=FULL; PRAGMA cache_size=-65536;";
        await setup.ExecuteNonQueryAsync();
        setup.CommandText = "PRAGMA synchronous;";
        Assert.Equal(2L, Convert.ToInt64(await setup.ExecuteScalarAsync()));
        setup.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", Convert.ToString(await setup.ExecuteScalarAsync()));
        await using var command = db.CreateCommand();
        command.CommandText = """
            WITH digits(n) AS (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)), numbers(i) AS (
              SELECT a.n+10*b.n+100*c.n+1000*d.n+10000*e.n+100000*f.n
              FROM digits a,digits b,digits c,digits d,digits e,digits f
            ), fixture AS (
              SELECT i, 'D:\Фото\' || (2000+i%27) || '\' || printf('%08d',i) || '.jpg' AS path,
                'D:/ФОТО/' || (2000+i%27) || '/' || printf('%08d',i) || '.JPG' AS key,
                $ticks+(i%3650)*864000000000 AS date FROM numbers
            )
            INSERT INTO desktop_media_items(asset_id,path,path_key,folder_key,search_key,size_bytes,
                file_modified_utc_ticks,file_local_ticks,file_month,capture_date_ticks,capture_month,
                metadata_indexed,metadata_status,last_seen_utc)
            SELECT printf('%032x',i+1),path,key,'D:/ФОТО/' || (2000+i%27),key,1000+i%100,
                date,date,strftime('%Y-%m','2015-01-01',printf('+%d days',i%3650)),
                CASE WHEN i%7=0 THEN NULL ELSE date END,
                CASE WHEN i%7=0 THEN NULL ELSE strftime('%Y-%m','2015-01-01',printf('+%d days',i%3650)) END,
                1,CASE WHEN i%7=0 THEN 2 ELSE 1 END,'2026-10-08T00:00:00Z' FROM fixture;
            """;
        command.Parameters.AddWithValue("$ticks", new DateTime(2015, 1, 1).Ticks);
        await command.ExecuteNonQueryAsync();
        setup.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await setup.ExecuteNonQueryAsync();
    }
}
