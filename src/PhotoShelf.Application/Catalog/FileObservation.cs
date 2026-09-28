namespace PhotoShelf.Application.Catalog;

public enum FileAvailability
{
    Available = 0,
    Missing = 1,
    RootOffline = 2,
    AccessDenied = 3,
    NeedsVerification = 4
}

/// <summary>A worker's read-only observation. Unavailable observations never replace known file metadata.</summary>
public sealed record FileObservation(SavedMediaItem Item, bool ForceContentRevalidation = false);
