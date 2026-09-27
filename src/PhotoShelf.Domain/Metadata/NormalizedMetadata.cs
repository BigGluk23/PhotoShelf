namespace PhotoShelf.Domain.Metadata;

public sealed record GeoCoordinate(double Latitude, double Longitude, double? AltitudeMeters)
{
    public GeoCoordinate Validate()
    {
        if (Latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(Latitude));
        }

        if (Longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(Longitude));
        }

        return this;
    }
}

public sealed record NormalizedMetadata(
    MetadataTimestamp? DateTimeOriginal = null,
    MetadataTimestamp? CreateDate = null,
    MetadataTimestamp? ModifyDate = null,
    GeoCoordinate? Location = null,
    string? CameraMake = null,
    string? CameraModel = null,
    string? LensMake = null,
    string? LensModel = null,
    double? FocalLengthMillimeters = null,
    double? ApertureFNumber = null,
    double? ExposureTimeSeconds = null,
    int? Iso = null,
    int? Orientation = null,
    int? PixelWidth = null,
    int? PixelHeight = null,
    string? ColorProfile = null,
    int? Rating = null,
    string? Title = null,
    string? Caption = null,
    string? Description = null,
    string? Author = null,
    string? Copyright = null,
    string? Software = null)
{
    public static NormalizedMetadata Empty { get; } = new();

    public NormalizedMetadata Validate()
    {
        if (Rating is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(Rating), "Rating must be from 0 to 5.");
        }

        Location?.Validate();
        return this;
    }
}

