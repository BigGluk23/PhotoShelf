namespace PhotoShelf.Application.Metadata;

// Stored values are part of the catalog format. Do not renumber them.
public enum MetadataReadStatus { Pending = 0, Found = 1, Absent = 2, Unsupported = 3, TransientError = 4, Corrupt = 5 }

public sealed record CaptureDateReadResult(MetadataReadStatus Status, DateTime? CaptureDate = null, string? ErrorCode = null)
{
    public bool IsAuthoritative => Status is MetadataReadStatus.Found or MetadataReadStatus.Absent;
    public bool IsTerminal => Status is not (MetadataReadStatus.Pending or MetadataReadStatus.TransientError);
    public DateTime? ApplyTo(DateTime? previous) => IsAuthoritative ? CaptureDate : previous;
    public DateTime? RetryAtUtc(DateTime attemptedUtc) => Status == MetadataReadStatus.TransientError ? attemptedUtc.AddMinutes(5) : null;
}
