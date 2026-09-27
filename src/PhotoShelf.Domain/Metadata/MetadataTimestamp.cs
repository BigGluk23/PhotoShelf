namespace PhotoShelf.Domain.Metadata;

public sealed record MetadataTimestamp(
    DateTime LocalValue,
    short? UtcOffsetMinutes,
    DatePrecision Precision)
{
    public DateTimeOffset? UtcInstant => UtcOffsetMinutes is { } minutes
        ? new DateTimeOffset(DateTime.SpecifyKind(LocalValue, DateTimeKind.Unspecified), TimeSpan.FromMinutes(minutes))
        : null;
}

