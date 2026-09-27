using PhotoShelf.Domain.Catalog;
using PhotoShelf.Domain.Metadata;

namespace PhotoShelf.Application.Metadata;

public interface ICompanionResolver
{
    ValueTask<IReadOnlyList<CompanionResolution>> ResolveAsync(
        IReadOnlyList<CompanionCandidate> candidates,
        CancellationToken cancellationToken = default);
}

public sealed record CompanionCandidate(
    MediaFile File,
    IReadOnlyList<MetadataUniqueId> UniqueIds);

public sealed record CompanionResolution(
    Guid PrimaryFileId,
    Guid CompanionFileId,
    AssetFileRole Role,
    CompanionConfidence Confidence,
    string Evidence);

public enum CompanionConfidence
{
    ExactIdentifier = 0,
    ExactMetadataRelationship = 1,
    UnambiguousFileNameFallback = 2,
    RequiresReview = 3
}

