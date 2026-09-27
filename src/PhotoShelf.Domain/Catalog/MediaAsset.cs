using PhotoShelf.Domain.Metadata;

namespace PhotoShelf.Domain.Catalog;

public sealed record MediaAsset(
    Guid Id,
    AssetKind Kind,
    string DisplayName,
    Guid? PrimaryFileId,
    NormalizedMetadata Metadata,
    IReadOnlyList<MediaFile> Files,
    IReadOnlyList<AssetFileLink> FileLinks,
    IReadOnlyList<RawMetadataProperty> RawMetadata,
    IReadOnlyList<string> Tags,
    DateTimeOffset CatalogedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public DateTimeOffset? EffectiveCaptureInstant => Metadata.DateTimeOriginal?.UtcInstant;
}

