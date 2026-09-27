using PhotoShelf.Domain.Catalog;
using PhotoShelf.Domain.Metadata;

namespace PhotoShelf.Application.Catalog;

public interface IPhotoCatalog
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask UpsertLibraryRootAsync(
        LibraryRoot root,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<LibraryRoot>> ListLibraryRootsAsync(
        CancellationToken cancellationToken = default);

    ValueTask UpsertAssetAsync(
        CatalogAssetDocument document,
        CancellationToken cancellationToken = default);

    ValueTask<MediaAsset?> GetAssetAsync(
        Guid assetId,
        CancellationToken cancellationToken = default);

    ValueTask<PhotoPage<MediaAsset>> SearchAsync(
        PhotoQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record CatalogAssetDocument(
    MediaAsset Asset,
    IReadOnlyList<MetadataUniqueId> UniqueIds);

public sealed record PhotoPage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    bool HasMore);
