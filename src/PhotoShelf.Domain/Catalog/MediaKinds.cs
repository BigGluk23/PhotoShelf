namespace PhotoShelf.Domain.Catalog;

public enum AssetKind
{
    StillImage = 0,
    LivePhoto = 1,
    RawImage = 2,
    Animation = 3,
    Video = 4,
    Unknown = 99
}

public enum MediaFileKind
{
    Image = 0,
    Video = 1,
    MetadataSidecar = 2,
    AdjustmentSidecar = 3,
    Unknown = 99
}

public enum AssetFileRole
{
    PrimaryImage = 0,
    AlternateImage = 1,
    PairedVideo = 2,
    MetadataSidecar = 3,
    AdjustmentSidecar = 4,
    EmbeddedPreview = 5,
    UnknownCompanion = 99
}

