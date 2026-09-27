namespace PhotoShelf.Domain.Catalog;

public sealed record FileSystemTimestamps(
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    DateTimeOffset? AccessedUtc);

public sealed record MediaFile(
    Guid Id,
    Guid LibraryRootId,
    string FullPath,
    string FileName,
    string Extension,
    MediaFileKind Kind,
    long ByteLength,
    FileSystemTimestamps FileSystemTimestamps,
    string? ContentHash = null,
    string? MimeType = null,
    bool IsOffline = false);

public sealed record AssetFileLink(
    Guid AssetId,
    Guid FileId,
    AssetFileRole Role,
    int SortOrder = 0,
    string? PairingEvidence = null);

