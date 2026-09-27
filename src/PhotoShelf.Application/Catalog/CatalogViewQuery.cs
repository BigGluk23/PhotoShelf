namespace PhotoShelf.Application.Catalog;

/// <summary>Immutable request captured before background work. Cursor belongs to exactly this view.</summary>
public sealed record CatalogViewQuery
{
    public string? Folder { get; init; }
    public bool IncludeSubfolders { get; init; } = true;
    public string ViewMode { get; init; } = "All";
    public bool ShowVideos { get; init; } = true;
    public bool IncludeSystemFolders { get; init; }
    public bool DuplicateCandidatesOnly { get; init; }
    public DateTime? MetadataDueAtUtc { get; init; }
    public bool MissingCaptureDateOnly { get; init; }
    public bool UseCaptureDate { get; init; }
    public bool NewestFirst { get; init; } = true;
    public string SearchText { get; init; } = "";
    public IReadOnlyList<string> ExcludedFolders { get; init; } = Array.Empty<string>();
    public int PageSize { get; init; } = 256;
    public int Offset { get; init; }
    public string? Cursor { get; init; }
    public string? GroupKey { get; init; }
}

public sealed record CatalogPage(IReadOnlyList<SavedMediaItem> Items, string? NextCursor, bool HasMore);
public sealed record CatalogDateGroup(string Key, int? Year, int? Month, long Count);
