using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class PagedCatalogTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"photoshelf-pages-"+Guid.NewGuid().ToString("N"));
    private async Task<SqliteDesktopCatalogStore> CreateAsync(params SavedMediaItem[] items)
    {
        var store=new SqliteDesktopCatalogStore(_root);await store.InitializeAsync();await store.UpsertItemsAsync(items);return store;
    }
    private static SavedMediaItem Item(string path,int day=1,bool favorite=false)=>new()
    {
        Path=path,SizeBytes=42,FileModifiedAt=new DateTime(2026,9,day,12,0,0,DateTimeKind.Local),IsFavorite=favorite
    };
    private static async Task<List<SavedMediaItem>> ReadAsync(SqliteDesktopCatalogStore store,CatalogViewQuery query)
    {
        var all=new List<SavedMediaItem>();await foreach(var item in store.EnumerateAsync(query))all.Add(item);return all;
    }

    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task KeysetPagingMatchesDatesAndUnicodePathsWithoutDuplicates(bool capture,bool descending)
    {
        var items=Enumerable.Range(0,701).Select(i=>new SavedMediaItem
        {
            Path=$"C:\\Фото\\{(i%2==0 ? "ёлка" : "Абрикос")}{i:D4}.jpg",SizeBytes=i,
            FileModifiedAt=i%7==0 ? null : new DateTime(2026,1+(i%4),1+(i%3),12,0,0,DateTimeKind.Local),
            CaptureDate=i%5==0 ? null : new DateTime(2020,1+(i%3),1+(i%4)),MetadataIndexed=true
        }).ToArray();
        var store=await CreateAsync(items);var query=new CatalogViewQuery{UseCaptureDate=capture,NewestFirst=descending,PageSize=37};
        var actual=await ReadAsync(store,query);Func<SavedMediaItem,long> date=x=>(capture ? x.CaptureDate : x.FileModifiedAt)?.Ticks??0;
        var ordered=descending ? items.OrderByDescending(date) : items.OrderBy(date);
        var expected=ordered.ThenBy(x=>x.Path.Replace('\\','/').ToUpperInvariant(),StringComparer.Ordinal).Select(x=>x.Path);
        Assert.Equal(expected,actual.Select(x=>x.Path));Assert.Equal(items.Length,actual.Select(x=>x.AssetId).Distinct().Count());
        var groups=await store.QueryGroupsAsync(query);Assert.Equal(items.Length,groups.Sum(x=>x.Count));
        foreach(var group in groups)
        {
            var page=await store.QueryPageAsync(query,0,1024,group.Key);
            Assert.Equal(group.Count,page.Items.Count);
            Assert.All(page.Items,x=>Assert.Equal(group.Month,(capture ? x.CaptureDate : x.FileModifiedAt)?.Month));
        }
    }

    [Fact]public async Task FolderBoundariesExclusionsUnicodeSearchAndFlagsStayInSql()
    {
        var favorite=Item("D:\\Фото\\день.jpg",favorite:true);var hidden=Item("D:\\Фото\\hidden.jpg");hidden.IsHiddenOrSystem=true;
        var video=Item("D:\\Фото\\video.mp4");video.IsVideo=true;
        var captured=Item("D:\\Фото\\Sub\\other.jpg");captured.CaptureDate=new DateTime(2025,1,1);captured.MetadataIndexed=true;
        var store=await CreateAsync(favorite,hidden,video,captured,Item("D:\\Фото2\\wrong.jpg"));
        var query=new CatalogViewQuery{Folder="d:\\фото",ViewMode="Folder",ExcludedFolders=new[]{"D:\\Фото"}};
        Assert.Equal(3,await store.CountAsync(query));
        Assert.Equal(2,await store.CountAsync(query with{IncludeSubfolders=false}));
        Assert.Equal(4,await store.CountAsync(query with{IncludeSystemFolders=true}));
        Assert.Equal("D:\\Фото\\день.jpg",Assert.Single((await store.QueryPageAsync(query with{SearchText="ДЕНЬ"})).Items).Path);
        Assert.Single((await store.QueryPageAsync(query with{MissingCaptureDateOnly=true})).Items);
        Assert.Equal(2,await store.CountAsync(query with{ShowVideos=false}));
        Assert.Single((await store.QueryPageAsync(new CatalogViewQuery{ViewMode="Favorites"})).Items);
        Assert.Single((await store.QueryPageAsync(new CatalogViewQuery{ExcludedFolders=new[]{"d:\\фото"}})).Items);
        var folders=await store.GetImmediateFoldersAsync("D:\\Фото");Assert.Equal(new[]{"D:\\Фото\\Sub"},folders);
    }

    [Fact]public async Task RecentLimitsByFileDateBeforeCaptureSortingAndGroupPaging()
    {
        var store=await CreateAsync(Enumerable.Range(0,600).Select(i=>new SavedMediaItem
        {
            Path=$"X:/archive/{i:D4}.jpg",SizeBytes=i,FileModifiedAt=new DateTime(2026,1,1).AddMinutes(i),
            CaptureDate=new DateTime(2020,1,1).AddDays(600-i),MetadataIndexed=true
        }).ToArray());
        var query=new CatalogViewQuery{ViewMode="Recent",UseCaptureDate=true,PageSize=73};
        var result=await ReadAsync(store,query);Assert.Equal(500,result.Count);Assert.Equal("X:/archive/0100.jpg",result[0].Path);
        Assert.Equal(500,await store.CountAsync(query));Assert.Equal(500,(await store.QueryGroupsAsync(query)).Sum(x=>x.Count));
    }

    [Fact]public async Task OffsetAndCursorAgreeAndCursorCannotLeakIntoAnotherView()
    {
        var store=await CreateAsync(Enumerable.Range(0,80).Select(i=>Item($"offline/{i:D4}.jpg")).ToArray());
        var query=new CatalogViewQuery{PageSize=31};var first=await store.QueryPageAsync(query);
        var next=await store.QueryPageAsync(query with{Cursor=first.NextCursor});var offset=await store.QueryPageAsync(query,31,31);
        Assert.Equal(offset.Items.Select(x=>x.AssetId),next.Items.Select(x=>x.AssetId));
        await Assert.ThrowsAsync<ArgumentException>(()=>store.QueryPageAsync(query with{Cursor=first.NextCursor,NewestFirst=false}));
    }

    [Fact]public async Task IncrementalSavePreservesOtherRowsFavoritesAndInvalidatesChangedMetadata()
    {
        var store=await CreateAsync(Item("a.jpg",favorite:true),Item("b.jpg"));
        await store.UpdateCaptureDateAsync("a.jpg",42,new DateTime(2026,9,1,12,0,0,DateTimeKind.Local),new DateTime(2020,1,1));
        var identity=(await store.GetItemAsync("a.jpg"))!.AssetId;
        await store.UpsertItemsAsync(new[]{Item("a.jpg")});
        var existing=await store.GetItemAsync("a.jpg");Assert.True(existing!.IsFavorite);Assert.True(existing.MetadataIndexed);Assert.Equal(identity,existing.AssetId);
        await store.UpsertItemsAsync(new[]{Item("a.jpg",2)});
        var changed=await store.GetItemAsync("a.jpg");Assert.Null(changed!.CaptureDate);Assert.False(changed.MetadataIndexed);Assert.True(changed.IsFavorite);
        await store.SaveAsync(new LocalCatalogState{Items=new(){Item("c.jpg")}});
        Assert.Equal(3,await store.CountAsync(new()));
        await store.SetFavoriteAsync("b.jpg",true);Assert.Equal(2,await store.CountAsync(new(){ViewMode="Favorites"}));
    }

    [Fact]public async Task MoveIsIdempotentPreservesIdentityAndQuarantineCanBeRestored()
    {
        var store=await CreateAsync(Item("source.jpg",favorite:true));
        var source=(await store.GetItemAsync("source.jpg"))!;
        await store.UpdateCaptureDateAsync(source.Path,source.SizeBytes,source.FileModifiedAt,new DateTime(2001,2,3));
        await store.MoveItemAsync("source.jpg","target.jpg");await store.MoveItemAsync("source.jpg","target.jpg");
        var moved=await store.GetItemAsync("target.jpg");Assert.Equal(source.AssetId,moved!.AssetId);Assert.True(moved.IsFavorite);
        await store.UpdateCaptureDateAsync("source.jpg",source.SizeBytes,source.FileModifiedAt,new DateTime(2030,1,1));
        Assert.Null(await store.GetItemAsync("source.jpg"));Assert.Equal(new DateTime(2001,2,3),(await store.GetItemAsync("target.jpg"))!.CaptureDate);
        await store.MoveItemAsync("target.jpg","quarantine/target.jpg",true);Assert.Equal(0,await store.CountAsync(new()));
        await store.MoveItemAsync("quarantine/target.jpg","source.jpg");Assert.Equal(1,await store.CountAsync(new()));
        var restored=(await store.GetItemAsync("source.jpg"))!;Assert.Equal(source.AssetId,restored.AssetId);Assert.True(restored.IsFavorite);
    }

    [Fact]public async Task ConflictingOrUnownedDestinationNeverOverwritesCatalogIdentity()
    {
        var store=await CreateAsync(Item("source.jpg",favorite:true),Item("target.jpg"));
        var source=await store.GetItemAsync("source.jpg");var target=await store.GetItemAsync("target.jpg");
        await Assert.ThrowsAsync<IOException>(()=>store.MoveItemAsync("source.jpg","target.jpg"));
        await Assert.ThrowsAsync<IOException>(()=>store.MoveItemAsync("absent.jpg","target.jpg"));
        Assert.Equal(source!.AssetId,(await store.GetItemAsync("source.jpg"))!.AssetId);
        Assert.Equal(target!.AssetId,(await store.GetItemAsync("target.jpg"))!.AssetId);Assert.Equal(2,await store.CountAsync(new()));
    }

    [Fact]public async Task StablePathAnchorFindsNewIndexAfterSortingAndRespectsFilter()
    {
        var store=await CreateAsync(Item("a.jpg",1),Item("b.jpg",2),Item("c.jpg",3));
        var query=new CatalogViewQuery();Assert.Equal(2,await store.IndexOfAsync(query,"a.jpg"));
        Assert.Equal(0,await store.IndexOfAsync(query with{NewestFirst=false},"a.jpg"));
        Assert.Null(await store.IndexOfAsync(query with{ViewMode="Favorites"},"a.jpg"));
        Assert.Equal(0,await store.IndexOfAsync(query with{SearchText="b.jpg"},"b.jpg"));
    }

    [Fact]public async Task DuplicateSizeCountersFollowRescanQuarantineAndRestore()
    {
        var store=await CreateAsync(Item("a.jpg"),Item("b.jpg"));var query=new CatalogViewQuery{DuplicateCandidatesOnly=true};
        Assert.Equal(2,await store.CountAsync(query));await store.MoveItemAsync("b.jpg","quarantine/b.jpg",true);
        Assert.Equal(0,await store.CountAsync(query));await store.MoveItemAsync("quarantine/b.jpg","b.jpg");
        Assert.Equal(2,await store.CountAsync(query));var changed=Item("b.jpg");changed.SizeBytes=43;
        await store.UpsertItemsAsync(new[]{changed});Assert.Equal(0,await store.CountAsync(query));
        changed.SizeBytes=42;await store.UpsertItemsAsync(new[]{changed});Assert.Equal(2,await store.CountAsync(query));
    }

    [Fact]public async Task HashLookupRejectsChangedFingerprint()
    {
        var store=new DuplicateHashStore(_root);await store.InitializeAsync();var modified=new DateTime(2026,9,1,12,0,0,DateTimeKind.Local);
        await store.SaveAsync(new[]{new SavedDuplicateHash{Path="a.jpg",SizeBytes=42,FileModifiedAt=modified,Hash="HASH"}});
        Assert.NotNull(await store.TryGetAsync("a.jpg",42,modified));Assert.Null(await store.TryGetAsync("a.jpg",43,modified));
        Assert.Null(await store.TryGetAsync("a.jpg",42,modified.AddSeconds(1)));Assert.Null(await store.TryGetAsync("a.jpg",42,null));
    }

    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
