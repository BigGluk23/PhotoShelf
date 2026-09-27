using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class CatalogMigrationTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"photoshelf-migrate-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_root,"catalog-v2.sqlite");
    private async Task CreateLegacyAsync(bool conflictingPaths=false)
    {
        Directory.CreateDirectory(_root);await using var connection=new SqliteConnection($"Data Source={Database}");await connection.OpenAsync();
        await using var command=connection.CreateCommand();command.CommandText="""
            PRAGMA journal_mode=WAL;
            CREATE TABLE desktop_media_items(path TEXT PRIMARY KEY,is_favorite INTEGER,size_bytes INTEGER,file_modified_utc_ticks INTEGER,is_video INTEGER,last_seen_utc TEXT);
            CREATE TABLE desktop_settings(key TEXT PRIMARY KEY,value TEXT);
            CREATE TABLE desktop_excluded_folders(path TEXT PRIMARY KEY);
            CREATE TABLE desktop_metadata_cache(path TEXT PRIMARY KEY,size_bytes INTEGER,file_modified_utc_ticks INTEGER,capture_date_ticks INTEGER,metadata_indexed_at_utc TEXT);
            INSERT INTO desktop_media_items VALUES('D:\Фото\a.jpg',1,42,$date,0,'old');
            INSERT INTO desktop_metadata_cache VALUES('D:\Фото\a.jpg',42,$date,$capture,'old');
            INSERT INTO desktop_settings VALUES('view_mode','Favorites');
            """;
        command.Parameters.AddWithValue("$date",new DateTime(2026,9,1,12,0,0,DateTimeKind.Local).ToUniversalTime().Ticks);
        command.Parameters.AddWithValue("$capture",new DateTime(2005,2,3).Ticks);await command.ExecuteNonQueryAsync();
        if(conflictingPaths){command.CommandText=@"INSERT INTO desktop_media_items SELECT 'd:\фото\A.jpg',0,size_bytes,file_modified_utc_ticks,0,'old' FROM desktop_media_items LIMIT 1;";await command.ExecuteNonQueryAsync();}
    }
    [Fact]public async Task MigrationBacksUpAndVerifiesWalDataBeforeTransformingAndRunsOnce()
    {
        await CreateLegacyAsync();var store=new SqliteDesktopCatalogStore(_root);await store.InitializeAsync();await store.InitializeAsync();
        var item=Assert.Single((await store.QueryPageAsync(new())).Items);Assert.True(item.IsFavorite);Assert.True(item.MetadataIndexed);
        Assert.Equal(new DateTime(2005,2,3),item.CaptureDate);Assert.NotEmpty(item.AssetId);
        Assert.Equal("Favorites",(await store.LoadAsync(includeItems:false)).ViewMode);
        var backup=Assert.Single(Directory.GetFiles(Path.Combine(_root,"backups"),"*.sqlite"));
        await using var connection=new SqliteConnection($"Data Source={backup};Mode=ReadOnly");await connection.OpenAsync();
        await using var check=connection.CreateCommand();check.CommandText="PRAGMA integrity_check;";Assert.Equal("ok",await check.ExecuteScalarAsync());
        check.CommandText=@"SELECT is_favorite FROM desktop_media_items WHERE path='D:\Фото\a.jpg';";Assert.Equal(1L,await check.ExecuteScalarAsync());
        check.CommandText="SELECT COUNT(*) FROM pragma_table_info('desktop_media_items') WHERE name='asset_id';";Assert.Equal(0L,await check.ExecuteScalarAsync());
    }
    [Fact]public async Task AmbiguousLegacyPathsAbortMigrationWithoutDroppingOriginalRows()
    {
        await CreateLegacyAsync(true);var store=new SqliteDesktopCatalogStore(_root);await Assert.ThrowsAsync<SqliteException>(()=>store.InitializeAsync());
        await using var connection=new SqliteConnection($"Data Source={Database};Mode=ReadOnly");await connection.OpenAsync();
        await using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM desktop_media_items;";Assert.Equal(2L,await command.ExecuteScalarAsync());
        command.CommandText="PRAGMA integrity_check;";Assert.Equal("ok",await command.ExecuteScalarAsync());
        Assert.Single(Directory.GetFiles(Path.Combine(_root,"backups"),"*.sqlite"));
    }
    [Fact]public async Task ManualBackupPublishesOnlyVerifiedCompletedSnapshot()
    {
        var store=new SqliteDesktopCatalogStore(_root);await store.InitializeAsync();
        await store.UpsertItemsAsync(new[]{new SavedMediaItem{Path="offline.jpg",SizeBytes=123,IsFavorite=true}});
        var path=await store.CreateBackupAsync();Assert.True(File.Exists(path));Assert.EndsWith(".sqlite",path);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root,"backups"),"*.pending"));
        await using var connection=new SqliteConnection($"Data Source={path};Mode=ReadOnly");await connection.OpenAsync();
        await using var command=connection.CreateCommand();command.CommandText="PRAGMA integrity_check;";Assert.Equal("ok",await command.ExecuteScalarAsync());
        command.CommandText="SELECT is_favorite FROM desktop_media_items WHERE path='offline.jpg';";Assert.Equal(1L,await command.ExecuteScalarAsync());
    }

    [Fact]public async Task CorruptCatalogCannotPublishAnApparentlyCompletedBackup()
    {
        var store=new SqliteDesktopCatalogStore(_root);await store.InitializeAsync();await store.SaveAsync(new LocalCatalogState());
        SqliteConnection.ClearAllPools();
        // Synthetic database only: destroy the database header to exercise the fail-closed path.
        await File.WriteAllBytesAsync(Database,new byte[4096]);
        var error=await Record.ExceptionAsync(()=>store.CreateBackupAsync());
        Assert.True(error is SqliteException or InvalidDataException);
        var backups=Path.Combine(_root,"backups");
        Assert.True(!Directory.Exists(backups)||Directory.GetFiles(backups,"*.sqlite").Length==0);
        Assert.Equal(new byte[4096],await File.ReadAllBytesAsync(Database));
    }

    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
