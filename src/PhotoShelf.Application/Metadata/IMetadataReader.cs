using PhotoShelf.Domain.Catalog;
using PhotoShelf.Domain.Metadata;

namespace PhotoShelf.Application.Metadata;

public interface IMetadataReader
{
    bool CanRead(MediaFile file);

    ValueTask<MetadataReadResult> ReadAsync(
        MediaFile file,
        CancellationToken cancellationToken = default);
}

public sealed record MetadataReadResult(
    NormalizedMetadata Normalized,
    IReadOnlyList<RawMetadataProperty> RawProperties,
    IReadOnlyList<MetadataUniqueId> UniqueIds,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<MetadataDiagnostic> Diagnostics);

public sealed record MetadataDiagnostic(
    string Code,
    string Message,
    bool IsError = false);

