namespace PhotoShelf.Application.Metadata;

public sealed class MetadataReadBudgetExceededException() : Exception("The metadata read budget was exceeded.");

/// <summary>
/// Read-only, cooperative bound around a synchronous parser. This limits actual reads,
/// not decoder allocations; it cannot interrupt a blocked native call or a parser that
/// does not touch the stream. Keep the caller's file lease until parsing really finishes.
/// </summary>
public sealed class MetadataReadBudgetStream : Stream
{
    public const long DefaultMaximumBytes = 32L * 1024 * 1024;
    public const int DefaultMaximumOperations = 100_000;
    public static readonly TimeSpan DefaultMaximumDuration = TimeSpan.FromSeconds(5);
    private readonly Stream _inner;
    private readonly CancellationToken _token;
    private readonly long _maximumBytes;
    private readonly int _maximumOperations;
    private readonly TimeSpan _maximumDuration;
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly bool _leaveOpen;
    private bool _exceeded;

    public MetadataReadBudgetStream(Stream inner, CancellationToken token = default,
        long maximumBytes = DefaultMaximumBytes, int maximumOperations = DefaultMaximumOperations,
        TimeSpan? maximumDuration = null, TimeProvider? timeProvider = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOperations);
        _maximumDuration = maximumDuration ?? DefaultMaximumDuration;
        if (_maximumDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        _inner = inner; _token = token; _maximumBytes = maximumBytes; _maximumOperations = maximumOperations;
        _time = timeProvider ?? TimeProvider.System; _started = _time.GetTimestamp(); _leaveOpen = leaveOpen;
    }

    public long BytesRead { get; private set; }
    public int Operations { get; private set; }
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length { get { EnsureWithinBudget(); return _inner.Length; } }
    public override long Position
    {
        get { EnsureWithinBudget(); return _inner.Position; }
        set => Seek(value, SeekOrigin.Begin);
    }

    // Recheck after a parser returns too: a library can catch stream exceptions or
    // spend time processing already-read bytes without another Read/Seek call.
    public void EnsureWithinBudget()
    {
        _token.ThrowIfCancellationRequested();
        if (_exceeded || _time.GetElapsedTime(_started) >= _maximumDuration) Exceed();
    }

    private void BeginOperation()
    {
        EnsureWithinBudget();
        if (Operations >= _maximumOperations) Exceed();
        Operations++;
    }

    private void Exceed() { _exceeded = true; throw new MetadataReadBudgetExceededException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        BeginOperation();
        if (buffer.IsEmpty) return 0;
        var remaining = _maximumBytes - BytesRead;
        if (remaining <= 0) Exceed();
        var read = _inner.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
        BytesRead += read;
        EnsureWithinBudget();
        return read;
    }
    public override int ReadByte()
    {
        Span<byte> value = stackalloc byte[1];
        return Read(value) == 0 ? -1 : value[0];
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        BeginOperation(); var position = _inner.Seek(offset, origin); EnsureWithinBudget(); return position;
    }
    public override void Flush() => EnsureWithinBudget();
    public override void SetLength(long value) => throw new NotSupportedException("Metadata streams are read-only.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Metadata streams are read-only.");
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen) _inner.Dispose();
        base.Dispose(disposing);
    }
}
