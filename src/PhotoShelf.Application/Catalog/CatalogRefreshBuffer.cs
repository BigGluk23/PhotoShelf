namespace PhotoShelf.Application.Catalog;

/// <summary>
/// UI-thread publication policy: expose the first block quickly, then keep an existing
/// view stable until a worker boundary or an explicit refresh. A publication only
/// acknowledges the revision it started with; concurrent writes remain pending.
/// </summary>
public sealed class CatalogRefreshBuffer
{
    private long _publishedRevision;
    private long _boundaryRevision;
    public long Revision { get; private set; }
    public bool HasPendingChanges => Revision > _publishedRevision;

    public void RecordChange() => Revision++;
    public void CompleteStage() => _boundaryRevision = Revision;
    public bool ShouldPublish(bool hasVisibleItems) => HasPendingChanges &&
        (!hasVisibleItems || _boundaryRevision > _publishedRevision);

    public void Published(long revision)
    {
        if (revision < 0 || revision > Revision) throw new ArgumentOutOfRangeException(nameof(revision));
        _publishedRevision = Math.Max(_publishedRevision, revision);
    }
}
