using PhotoShelf.Domain.Metadata;

namespace PhotoShelf.Domain.Tests;

public sealed class MetadataTests
{
    [Fact]
    public void MetadataTimestampWithoutOffsetDoesNotInventUtcInstant()
    {
        var timestamp = new MetadataTimestamp(
            new DateTime(2024, 2, 3, 12, 30, 0, DateTimeKind.Unspecified),
            UtcOffsetMinutes: null,
            DatePrecision.Second);

        Assert.Null(timestamp.UtcInstant);
    }

    [Fact]
    public void MetadataTimestampWithOffsetCalculatesUtcInstant()
    {
        var timestamp = new MetadataTimestamp(
            new DateTime(2024, 2, 3, 12, 30, 0, DateTimeKind.Unspecified),
            UtcOffsetMinutes: 180,
            DatePrecision.Second);

        Assert.Equal(
            new DateTimeOffset(2024, 2, 3, 9, 30, 0, TimeSpan.Zero),
            timestamp.UtcInstant?.ToUniversalTime());
    }

    [Fact]
    public void RatingOutsideSupportedRangeIsRejected()
    {
        var metadata = new NormalizedMetadata(Rating: 6);

        Assert.Throws<ArgumentOutOfRangeException>(() => metadata.Validate());
    }

    [Fact]
    public void CoordinatesOutsideEarthBoundsAreRejected()
    {
        var coordinate = new GeoCoordinate(91, 20, null);

        Assert.Throws<ArgumentOutOfRangeException>(() => coordinate.Validate());
    }
}

