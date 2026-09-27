namespace PhotoShelf.Domain.Catalog;

public sealed record LibraryRoot(
    Guid Id,
    string DisplayName,
    string AbsolutePath,
    string PathKey,
    string? VolumeIdentity,
    bool IsAvailable,
    bool IncludeSubfolders,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

