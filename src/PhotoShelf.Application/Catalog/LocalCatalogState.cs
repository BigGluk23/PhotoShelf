using System.IO;
using System.Text.Json;

namespace PhotoShelf.Application.Catalog;

public sealed class LocalCatalogState
{
    public int Version { get; set; } = 1;

    public double TileWidth { get; set; } = 178;

    public bool ShowVideos { get; set; } = true;

    public bool IncludeSystemFolders { get; set; }

    public string DateGroupingMode { get; set; } = "FileDate";

    public string? ActiveFolder { get; set; }
    public string ViewMode { get; set; } = "All";
    public bool SortNewestFirst { get; set; } = true;
    public bool IncludeSubfolders { get; set; } = true;
    public List<string> ExpandedFolders { get; set; } = new();
    public string? QuarantineDirectory { get; set; }
    public List<string> QuarantineBatchDirectories { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ReadItemsFromSqlite { get; set; }

    public List<SavedMediaItem> Items { get; set; } = new();

    public List<string> ExcludedFolders { get; set; } = new();

    public List<SavedDuplicateHash> DuplicateHashes { get; set; } = new();
}

public sealed class SavedMediaItem
{
    public string AssetId { get; set; } = string.Empty;
    public DateTime? CaptureDate { get; set; }
    public bool MetadataIndexed { get; set; }
    public PhotoShelf.Application.Metadata.MetadataReadStatus MetadataStatus { get; set; }
    public DateTime? MetadataAttemptedAtUtc { get; set; }
    public DateTime? MetadataRetryAtUtc { get; set; }
    public string? MetadataErrorCode { get; set; }
    public bool IsHiddenOrSystem { get; set; }
    public string Path { get; set; } = string.Empty;

    public bool IsFavorite { get; set; }
    public bool IsVideo { get; set; }
    public long SizeBytes { get; set; }
    public DateTime? FileModifiedAt { get; set; }
}

public sealed class SavedDuplicateHash
{
    public string Path { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTime? FileModifiedAt { get; set; }

    public string Hash { get; set; } = string.Empty;
}
