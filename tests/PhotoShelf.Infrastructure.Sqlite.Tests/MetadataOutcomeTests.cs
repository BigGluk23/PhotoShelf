using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Metadata;

namespace PhotoShelf.Infrastructure.Sqlite.Tests;

public sealed class MetadataOutcomeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-metadata-test-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _modified = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
    private readonly DateTime _capture = new(2020, 1, 2);
    private async Task<SqliteDesktopCatalogStore> CreateAsync()
    {
        var store = new SqliteDesktopCatalogStore(_root); await store.InitializeAsync();
        await store.UpsertItemsAsync([new SavedMediaItem { Path = "test.jpg", SizeBytes = 42, FileModifiedAt = _modified, CaptureDate = _capture, MetadataIndexed = true }]);
        return store;
    }

    [Theory]
    [InlineData(MetadataReadStatus.TransientError)]
    [InlineData(MetadataReadStatus.Corrupt)]
    [InlineData(MetadataReadStatus.Unsupported)]
    public async Task FailedReindexPreservesConfirmedDateAndDiagnosticSurvivesReopen(MetadataReadStatus status)
    {
        var store = await CreateAsync(); await store.ResetMetadataIndexAsync();
        var attempted = new DateTime(2026,9,27,13,0,0,DateTimeKind.Utc);
        Assert.True(await store.UpdateMetadataResultAsync("test.jpg",42,_modified,new(status,ErrorCode:"read-error"),attempted));
        var saved = (await new SqliteDesktopCatalogStore(_root).GetItemAsync("test.jpg"))!;
        Assert.Equal(_capture,saved.CaptureDate); Assert.Equal(status,saved.MetadataStatus);
        Assert.Equal("read-error",saved.MetadataErrorCode);Assert.Equal(attempted,saved.MetadataAttemptedAtUtc);
        Assert.Equal(status==MetadataReadStatus.TransientError ? attempted.AddMinutes(5) : (DateTime?)null,saved.MetadataRetryAtUtc);
    }

    [Fact] public async Task TransientErrorRetriesOnlyWhenDueAndSuccessClearsError()
    {
        var store=await CreateAsync();var now=DateTime.UtcNow;
        await store.UpdateMetadataResultAsync("test.jpg",42,_modified,new(MetadataReadStatus.TransientError,ErrorCode:"locked"),now);
        Assert.Equal(0,await store.CountAsync(new(){MetadataDueAtUtc=now.AddMinutes(4)}));
        Assert.Equal(1,await store.CountAsync(new(){MetadataDueAtUtc=now.AddMinutes(5)}));
        await store.UpdateMetadataResultAsync("test.jpg",42,_modified,new(MetadataReadStatus.Found,_capture),now.AddMinutes(5));
        var saved=(await store.GetItemAsync("test.jpg"))!;Assert.Null(saved.MetadataErrorCode);Assert.Null(saved.MetadataRetryAtUtc);
        Assert.Equal(0,await store.CountAsync(new(){MetadataDueAtUtc=now.AddHours(1)}));
    }

    [Fact] public async Task AbsentIsAuthoritativeButFingerprintMismatchCannotEraseDate()
    {
        var store=await CreateAsync();
        Assert.False(await store.UpdateMetadataResultAsync("test.jpg",43,_modified,new(MetadataReadStatus.Absent),DateTime.UtcNow));
        Assert.Equal(_capture,(await store.GetItemAsync("test.jpg"))!.CaptureDate);
        Assert.True(await store.UpdateMetadataResultAsync("test.jpg",42,_modified,new(MetadataReadStatus.Absent),DateTime.UtcNow));
        Assert.Null((await store.GetItemAsync("test.jpg"))!.CaptureDate);
    }

    [Fact] public async Task RescanPreservesRetryForSameFileAndInvalidatesItForChangedFile()
    {
        var store=await CreateAsync();var now=DateTime.UtcNow;
        await store.UpdateMetadataResultAsync("test.jpg",42,_modified,new(MetadataReadStatus.TransientError,ErrorCode:"locked"),now);
        await store.UpsertItemsAsync([new(){Path="test.jpg",SizeBytes=42,FileModifiedAt=_modified}]);
        Assert.Equal(now.AddMinutes(5),(await store.GetItemAsync("test.jpg"))!.MetadataRetryAtUtc);
        await store.UpsertItemsAsync([new(){Path="test.jpg",SizeBytes=43,FileModifiedAt=_modified}]);
        var saved=(await store.GetItemAsync("test.jpg"))!;Assert.Null(saved.MetadataRetryAtUtc);Assert.Null(saved.CaptureDate);
        Assert.Equal(MetadataReadStatus.Pending,saved.MetadataStatus);
    }

    [Fact] public async Task V3MigrationBacksUpAndRechecksOldNullsWithoutLosingKnownDates()
    {
        var store=await CreateAsync();
        await store.UpsertItemsAsync([new(){Path="no-date.jpg",SizeBytes=1,MetadataIndexed=true}]);
        using(var connection=new SqliteConnection($"Data Source={Path.Combine(_root,"catalog-v2.sqlite")}"))
        {
            connection.Open();using var command=connection.CreateCommand();
            command.CommandText="""
                DROP INDEX ix_desktop_metadata_due;
                DROP INDEX ix_desktop_metadata_queue;
                ALTER TABLE desktop_media_items DROP COLUMN metadata_status;
                ALTER TABLE desktop_media_items DROP COLUMN metadata_attempted_ticks;
                ALTER TABLE desktop_media_items DROP COLUMN metadata_retry_ticks;
                ALTER TABLE desktop_media_items DROP COLUMN metadata_error_code;
                UPDATE desktop_settings SET value='3' WHERE key='catalog_schema';
                """;command.ExecuteNonQuery();
        }
        await store.InitializeAsync();
        Assert.Single(Directory.GetFiles(Path.Combine(_root,"backups"),"catalog-before-metadata-status-*.sqlite"));
        var known=(await store.GetItemAsync("test.jpg"))!;Assert.Equal(_capture,known.CaptureDate);Assert.True(known.MetadataIndexed);
        Assert.False((await store.GetItemAsync("no-date.jpg"))!.MetadataIndexed);
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
