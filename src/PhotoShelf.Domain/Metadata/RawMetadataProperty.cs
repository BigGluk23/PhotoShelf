namespace PhotoShelf.Domain.Metadata;

public sealed record RawMetadataProperty(
    Guid Id,
    Guid AssetId,
    Guid? SourceFileId,
    MetadataSourceKind SourceKind,
    string Namespace,
    string? Group,
    string Key,
    MetadataValueKind ValueKind,
    string? RawText,
    byte[]? RawBytes,
    string? NormalizedText,
    string? Language,
    int Ordinal = 0,
    string? BinaryDigest = null,
    long? BinaryLength = null,
    string? BinaryLocator = null);

public sealed record MetadataUniqueId(
    string Kind,
    string Value,
    Guid? SourceFileId,
    MetadataSourceKind SourceKind);

