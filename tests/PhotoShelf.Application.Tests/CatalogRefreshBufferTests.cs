using PhotoShelf.Application.Catalog;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class CatalogRefreshBufferTests
{
    [Fact]
    public void FirstBlockCanAppearBeforeScanFinishesButLaterBatchesLeaveVisibleViewStable()
    {
        var buffer = new CatalogRefreshBuffer();
        buffer.RecordChange();
        Assert.True(buffer.ShouldPublish(hasVisibleItems: false));
        buffer.Published(buffer.Revision);
        for (var batch = 0; batch < 1000; batch++)
        {
            buffer.RecordChange();
            Assert.False(buffer.ShouldPublish(hasVisibleItems: true));
        }
        Assert.True(buffer.HasPendingChanges);
        buffer.CompleteStage();
        Assert.True(buffer.ShouldPublish(hasVisibleItems: true));
        buffer.Published(buffer.Revision);
        Assert.False(buffer.HasPendingChanges);
        Assert.False(buffer.ShouldPublish(hasVisibleItems: true));
    }

    [Fact]
    public void WriteDuringPublicationIsNotAccidentallyAcknowledged()
    {
        var buffer = new CatalogRefreshBuffer();
        buffer.RecordChange(); buffer.CompleteStage();
        var publishing = buffer.Revision;
        buffer.RecordChange();
        buffer.Published(publishing);
        Assert.True(buffer.HasPendingChanges);
        Assert.False(buffer.ShouldPublish(hasVisibleItems: true));
        buffer.CompleteStage();
        Assert.True(buffer.ShouldPublish(hasVisibleItems: true));
    }

    [Fact]
    public void BoundaryArrivingDuringPublicationIsRetriedAfterItFinishes()
    {
        var buffer = new CatalogRefreshBuffer();
        buffer.RecordChange();
        var publishing = buffer.Revision;
        buffer.RecordChange(); buffer.CompleteStage();
        buffer.Published(publishing);
        Assert.True(buffer.ShouldPublish(hasVisibleItems: true));
        buffer.Published(buffer.Revision);
        buffer.Published(publishing); // A late older acknowledgement cannot undo the newer one.
        Assert.False(buffer.HasPendingChanges);
    }

    [Fact]
    public void UnchangedWorkerStageDoesNotCauseAnotherPublication()
    {
        var buffer = new CatalogRefreshBuffer();
        buffer.CompleteStage();
        Assert.False(buffer.ShouldPublish(hasVisibleItems: false));
        buffer.RecordChange(); buffer.Published(buffer.Revision);
        buffer.CompleteStage();
        Assert.False(buffer.ShouldPublish(hasVisibleItems: true));
    }
}
