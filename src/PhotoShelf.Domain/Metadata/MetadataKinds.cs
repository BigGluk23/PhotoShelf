namespace PhotoShelf.Domain.Metadata;

public enum MetadataSourceKind
{
    EmbeddedExif = 0,
    EmbeddedXmp = 1,
    EmbeddedIptc = 2,
    Container = 3,
    XmpSidecar = 4,
    AppleAae = 5,
    UserOverride = 6,
    Derived = 7,
    Unknown = 99
}

public enum MetadataValueKind
{
    Text = 0,
    Integer = 1,
    Real = 2,
    Boolean = 3,
    DateTime = 4,
    Rational = 5,
    Binary = 6,
    Json = 7,
    Unknown = 99
}

public enum DatePrecision
{
    Year = 0,
    Month = 1,
    Day = 2,
    Minute = 3,
    Second = 4,
    Subsecond = 5
}

