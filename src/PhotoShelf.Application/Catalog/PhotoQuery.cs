namespace PhotoShelf.Application.Catalog;

public enum PhotoSort
{
    CaptureDateDescending = 0,
    CaptureDateAscending = 1,
    FileNameAscending = 2,
    RatingDescending = 3,
    CatalogedDescending = 4
}

public sealed record GeoBounds(
    double SouthLatitude,
    double WestLongitude,
    double NorthLatitude,
    double EastLongitude);

public sealed record PhotoQuery(
    string? Text = null,
    Guid? LibraryRootId = null,
    string? PathPrefix = null,
    DateOnly? CaptureDateFrom = null,
    DateOnly? CaptureDateTo = null,
    DateTimeOffset? CapturedFrom = null,
    DateTimeOffset? CapturedTo = null,
    IReadOnlySet<string>? CameraModels = null,
    IReadOnlySet<string>? LensModels = null,
    int? MinimumRating = null,
    IReadOnlySet<string>? Tags = null,
    GeoBounds? MapBounds = null,
    PhotoSort Sort = PhotoSort.CaptureDateDescending,
    int PageSize = 200,
    string? Cursor = null)
{
    public PhotoQuery Validate()
    {
        if (PageSize is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(PageSize));
        }

        if (MinimumRating is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumRating));
        }

        if (CaptureDateFrom.HasValue &&
            CaptureDateTo.HasValue &&
            CaptureDateFrom.Value > CaptureDateTo.Value)
        {
            throw new ArgumentException("Capture date range is inverted.");
        }

        return this;
    }
}
