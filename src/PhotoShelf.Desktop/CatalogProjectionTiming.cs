namespace PhotoShelf.Desktop;

// Durations include worker/dispatcher scheduling. No search text or media paths.
internal sealed record CatalogProjectionTiming(
    long OperationId, string Outcome, double PreparationMs, double DebounceMs,
    double GroupsMs, double AnchorMs, double PrimeMs, double PublishMs, double TotalMs);
