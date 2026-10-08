namespace PhotoShelf.Application.Metadata;

// Stored values are part of the catalog format. Do not renumber them.
public enum MetadataReadStatus { Pending = 0, Found = 1, Absent = 2, Unsupported = 3, TransientError = 4, Corrupt = 5 }

public sealed record CaptureDateReadResult(MetadataReadStatus Status, DateTime? CaptureDate = null, string? ErrorCode = null, TimeSpan? RetryDelay = null)
{
    public bool IsAuthoritative => Status is MetadataReadStatus.Found or MetadataReadStatus.Absent;
    public bool IsTerminal => Status is not (MetadataReadStatus.Pending or MetadataReadStatus.TransientError);
    public DateTime? ApplyTo(DateTime? previous) => IsAuthoritative ? CaptureDate : previous;
    public DateTime? RetryAtUtc(DateTime attemptedUtc) => Status == MetadataReadStatus.TransientError ? attemptedUtc.Add(RetryDelay ?? TimeSpan.FromMinutes(5)) : null;
    // The previous interval is already durable in schema 5. No new schema or in-memory
    // per-file dictionary: backoff survives restarts and resets with a new observation.
    public CaptureDateReadResult WithRetryBackoff(DateTime? previousAttempt, DateTime? previousRetry)
    {
        if (Status != MetadataReadStatus.TransientError) return this;
        var interval = previousRetry - previousAttempt;
        var minutes = interval?.TotalMinutes ?? 0;
        var next = minutes < 5 ? 5 : minutes < 30 ? 30 : minutes < 360 ? 360 : 1440;
        return this with { RetryDelay = TimeSpan.FromMinutes(next) };
    }
}
