using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Files;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

/// <summary>Exercise the actual two durable stores together, including the uncertain callback-return window.</summary>
public sealed class FileMoveCatalogIntegrationTests : IDisposable
{
    private readonly string _root=Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),"photoshelf-move-db-"+Guid.NewGuid().ToString("N"));
    private string CatalogDirectory=>Path.Combine(_root,"catalog");
    private string Journal=>Path.Combine(_root,"operations","move.jsonl");
    private string Destination=>Path.Combine(_root,"destination");
    private string Write(string name,byte[] bytes)
    {
        var path=Path.Combine(_root,"source",name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,bytes);return path;
    }
    private async Task<SqliteDesktopCatalogStore> CatalogAsync(params string[] paths)
    {
        var store=new SqliteDesktopCatalogStore(CatalogDirectory);await store.InitializeAsync();
        await store.UpsertItemsAsync(paths.Select(path=>new SavedMediaItem
        {
            Path=path,SizeBytes=new FileInfo(path).Length,FileModifiedAt=File.GetLastWriteTime(path),
            IsFavorite=true,CaptureDate=new DateTime(2007,4,5),MetadataIndexed=true
        }));return store;
    }
    private static FileMoveService Service(bool copy)=>new(new FileMoveOptions{AlwaysCopy=copy});
    private static readonly byte[] Photo=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0XcAAAAASUVORK5CYII=");

    [Theory][InlineData(false)][InlineData(true)]
    public async Task CrashAfterSqliteCommitBeforeJournalAcknowledgementReplaysDurableReceipt(bool copy)
    {
        var source=Write("photo.png",Photo);var catalog=await CatalogAsync(source);
        var identity=(await catalog.GetItemAsync(source))!.AssetId;var service=Service(copy);
        var plan=service.Plan(new[]{new MoveRequest(source,null)},Destination,CollisionPolicy.Skip);
        Assert.All(plan,entry=>Assert.Null(entry.SkipReason));
        await Assert.ThrowsAsync<MoveInterruptionException>(()=>service.ExecuteAsync(plan,Journal,async entry=>
        {
            await catalog.MoveItemAsync(entry.Source,entry.Destination);
            throw new MoveInterruptionException("process died after FULL SQLite commit, before journal catalog_committed");
        },null,default));
        Assert.Equal(identity,(await catalog.GetItemAsync(plan[0].Destination))!.AssetId);
        Assert.Equal(Photo,await File.ReadAllBytesAsync(plan[0].Destination));
        // Release native handles and construct both services again to model restart.
        SqliteConnection.ClearAllPools();catalog=await CatalogAsync();service=Service(copy);var callbacks=0;
        async Task Commit(MoveEntry entry){callbacks++;await catalog.MoveItemAsync(entry.Source,entry.Destination);}
        Assert.True(Assert.Single(await service.RecoverAsync(Journal,Commit,null,default)).Moved);
        Assert.Equal(1,callbacks);Assert.Null(await catalog.GetItemAsync(source));
        var item=(await catalog.GetItemAsync(plan[0].Destination))!;Assert.Equal(identity,item.AssetId);Assert.True(item.IsFavorite);
        Assert.Equal(new DateTime(2007,4,5),item.CaptureDate);Assert.Equal(Photo,await File.ReadAllBytesAsync(item.Path));
        Assert.True(Assert.Single(await service.RecoverAsync(Journal,Commit,null,default)).Moved);
        Assert.Equal(1,callbacks); // Completed recovery is not applied a second time.
        Assert.Equal(1,await catalog.CountAsync(new()));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task QuarantineAndUndoPreserveBytesStableIdentityFavoritesDatesAndCompanion(bool copy)
    {
        var source=Write("photo.png",Photo);var sidecarBytes=System.Text.Encoding.UTF8.GetBytes("<xmp>untouched tags</xmp>");
        var sidecar=Write("photo.xmp",sidecarBytes);var catalog=await CatalogAsync(source);
        var before=(await catalog.GetItemAsync(source))!;var service=Service(copy);var quarantine=Path.Combine(_root,"quarantine");
        Task Commit(MoveEntry entry)=>catalog.MoveItemAsync(entry.Source,entry.Destination,entry.Destination.StartsWith(quarantine+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));
        var plan=service.Plan(new[]{new MoveRequest(source,null)},quarantine,CollisionPolicy.Skip);
        Assert.Equal(2,plan.Count);Assert.All(await service.ExecuteAsync(plan,Journal,Commit,null,default),result=>Assert.True(result.Moved,result.Error));
        Assert.Equal(0,await catalog.CountAsync(new()));Assert.False(File.Exists(source));Assert.False(File.Exists(sidecar));
        var undo=await service.UndoAsync(Journal,Path.Combine(_root,"operations","undo.jsonl"),Commit,null,default);
        Assert.Equal(2,undo.Count);Assert.All(undo,result=>Assert.True(result.Moved,result.Error));
        Assert.Equal(Photo,await File.ReadAllBytesAsync(source));Assert.Equal(sidecarBytes,await File.ReadAllBytesAsync(sidecar));
        var restored=(await catalog.GetItemAsync(source))!;Assert.Equal(before.AssetId,restored.AssetId);Assert.True(restored.IsFavorite);
        Assert.Equal(before.CaptureDate,restored.CaptureDate);Assert.Equal(1,await catalog.CountAsync(new()));
        Assert.True(service.ReadHistory(Path.GetDirectoryName(Journal)!).Single(x=>x.JournalPath==Journal).IsUndone);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task PartialCompanionCatalogCommitRecoversAllMembersWithoutClobberingIdentity(bool copy)
    {
        var rawBytes=System.Text.Encoding.UTF8.GetBytes("synthetic RAW bytes");var tags=System.Text.Encoding.UTF8.GetBytes("synthetic XMP bytes");
        var raw=Write("pair.dng",rawBytes);var jpeg=Write("pair.jpg",Photo);var xmp=Write("pair.xmp",tags);
        var expected=new Dictionary<string,byte[]>{{raw,rawBytes},{jpeg,Photo},{xmp,tags}};
        var catalog=await CatalogAsync(raw,jpeg);var rawId=(await catalog.GetItemAsync(raw))!.AssetId;var jpegId=(await catalog.GetItemAsync(jpeg))!.AssetId;
        var service=Service(copy);var plan=service.Plan(new[]{new MoveRequest(raw,null)},Destination,CollisionPolicy.Skip);Assert.Equal(3,plan.Count);Assert.All(plan,entry=>Assert.Null(entry.SkipReason));
        var attempts=0;
        var failed=await service.ExecuteAsync(plan,Journal,async entry=>
        {
            if(++attempts==2)throw new IOException("injected SQLite error before second companion catalog commit");
            await catalog.MoveItemAsync(entry.Source,entry.Destination);
        },null,default);
        Assert.All(failed,result=>Assert.False(result.Moved));
        foreach(var entry in plan)Assert.Equal(expected[entry.Source],await File.ReadAllBytesAsync(entry.Destination));
        SqliteConnection.ClearAllPools();catalog=await CatalogAsync();
        Task Commit(MoveEntry entry)=>catalog.MoveItemAsync(entry.Source,entry.Destination);
        var recovered=await Service(copy).RecoverAsync(Journal,Commit,null,default);Assert.Equal(3,recovered.Count);
        Assert.All(recovered,result=>Assert.True(result.Moved,result.Error));
        foreach(var entry in plan){Assert.False(File.Exists(entry.Source));Assert.Equal(expected[entry.Source],await File.ReadAllBytesAsync(entry.Destination));}
        var rawTarget=plan.Single(x=>x.Source==raw).Destination;var jpegTarget=plan.Single(x=>x.Source==jpeg).Destination;
        Assert.Equal(rawId,(await catalog.GetItemAsync(rawTarget))!.AssetId);Assert.Equal(jpegId,(await catalog.GetItemAsync(jpegTarget))!.AssetId);
        Assert.All((await catalog.QueryPageAsync(new())).Items,item=>Assert.True(item.IsFavorite));Assert.Equal(2,await catalog.CountAsync(new()));
        Assert.All(await Service(copy).RecoverAsync(Journal,Commit,null,default),result=>Assert.True(result.Moved,result.Error));
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
