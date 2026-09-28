using PhotoShelf.Application.Catalog;
using PhotoShelf.Desktop;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

public sealed class PhotoItemObservationTests
{
    [Fact]
    public void ExternalRenameKeepsSelectedObjectAndLateOldSnapshotCannotUndoIt()
    {
        var item = new PhotoItem(@"C:\synthetic\old.jpg", 42, null) { IsSelected = true };
        var before = new SavedMediaItem { Path = item.Path, AssetId = "stable-asset", SizeBytes = 42,
            IsFavorite = true, ObservationVersion = 1, Availability = FileAvailability.Available };
        item.ApplyCatalogObservation(before);
        item.ApplyCatalogObservation(new SavedMediaItem { Path = @"C:\synthetic\new.jpg", AssetId = before.AssetId,
            SizeBytes = 42, IsFavorite = true, ObservationVersion = 2, Availability = FileAvailability.Available });
        item.ApplyCatalogObservation(before);
        Assert.Equal(@"C:\synthetic\new.jpg", item.Path);
        Assert.Equal("new.jpg", item.FileName);
        Assert.True(item.IsSelected); Assert.True(item.IsFavorite); Assert.Equal(2, item.ObservationVersion);
    }

    [Theory]
    [InlineData(FileAvailability.RootOffline, null)]
    [InlineData(FileAvailability.Missing, null)]
    [InlineData(FileAvailability.AccessDenied, "AccessDenied")]
    [InlineData(FileAvailability.NeedsVerification, "ReparsePoint")]
    public void UnavailableObservationSuppressesPreviewButRetainsKnownFileDetails(FileAvailability state, string? error)
    {
        var date = new DateTime(2020, 1, 2);
        var item = new PhotoItem(@"C:\synthetic\image.jpg", 42, date);
        item.ApplyCatalogObservation(new SavedMediaItem { Path = item.Path, AssetId = "stable", SizeBytes = 42,
            FileModifiedAt = date, CaptureDate = date, MetadataIndexed = true, IsFavorite = true,
            Availability = state, AvailabilityErrorCode = error, ObservationVersion = 2 });
        Assert.Null(item.PreviewPath); Assert.NotEmpty(item.AvailabilityText);
        Assert.Equal(date, item.CaptureDate); Assert.Equal(42, item.FileSizeBytes); Assert.True(item.IsFavorite);
    }
}
