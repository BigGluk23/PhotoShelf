using System.Security.Cryptography;
using PhotoShelf.Application.Metadata;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class MetadataReadBudgetStreamTests
{
    [Fact]
    public void ReadBytesAreCappedAcrossSeeksAndReReads()
    {
        using var inner = new MemoryStream([1, 2, 3, 4, 5, 6]);
        using var stream = new MetadataReadBudgetStream(inner, maximumBytes: 5, leaveOpen: true);
        var buffer = new byte[3];
        Assert.Equal(3, stream.Read(buffer, 0, buffer.Length));
        stream.Position = 0;
        Assert.Equal(2, stream.Read(buffer));
        Assert.Equal(5, stream.BytesRead);
        Assert.Equal(2, inner.Position);
        Assert.Throws<MetadataReadBudgetExceededException>(() => stream.ReadByte());
        Assert.Equal(2, inner.Position);
        // If a parser swallows the original exception, completion still cannot become Absent.
        Assert.Throws<MetadataReadBudgetExceededException>(stream.EnsureWithinBudget);
    }

    [Fact]
    public void SeekAndEmptyReadsConsumeOperationBudgetWithoutReadingPayload()
    {
        using var inner = new MemoryStream(new byte[100]);
        using var stream = new MetadataReadBudgetStream(inner, maximumOperations: 2);
        Assert.Equal(50, stream.Seek(50, SeekOrigin.Begin));
        Assert.Equal(0, stream.Read(Array.Empty<byte>(), 0, 0));
        Assert.Throws<MetadataReadBudgetExceededException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Equal(50, inner.Position); Assert.Equal(0, stream.BytesRead); Assert.Equal(2, stream.Operations);
    }

    [Fact]
    public void CancellationPreventsReadAndSeekAndDoesNotMasqueradeAsBudgetExhaustion()
    {
        using var inner = new MemoryStream([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        using var stream = new MetadataReadBudgetStream(inner, cancellation.Token);
        Assert.Equal(1, stream.ReadByte()); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => stream.ReadByte());
        Assert.Throws<OperationCanceledException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Equal(1, inner.Position); Assert.Equal(1, stream.BytesRead);
    }

    [Fact]
    public void DurationIsCheckedBeforeAnOperationAndAgainAfterAReadActuallyReturns()
    {
        var clock = new Clock();
        using var inner = new MemoryStream([1, 2, 3]);
        using var stream = new MetadataReadBudgetStream(inner, timeProvider: clock);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Throws<MetadataReadBudgetExceededException>(() => stream.ReadByte());
        Assert.Equal(0, inner.Position);

        var slowClock = new Clock();
        using var slowInner = new SlowStream(() => slowClock.Advance(TimeSpan.FromSeconds(6)));
        using var slow = new MetadataReadBudgetStream(slowInner, timeProvider: slowClock);
        Assert.Throws<MetadataReadBudgetExceededException>(() => slow.ReadByte());
        Assert.Equal(1, slow.BytesRead); Assert.Equal(1, slowInner.Position);
    }

    [Fact]
    public void OriginalBytesAndModificationTimeAreUnchangedAndLeaseEndsOnlyOnDispose()
    {
        var path = Path.Combine(Path.GetTempPath(), "photoshelf-read-budget-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = new byte[] { 1, 2, 3, 4 }; File.WriteAllBytes(path, bytes);
        var modified = File.GetLastWriteTimeUtc(path);
        try
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var stream = new MetadataReadBudgetStream(file, maximumBytes: 1))
            {
                Assert.Equal(1, stream.ReadByte());
                Assert.Throws<MetadataReadBudgetExceededException>(() => stream.ReadByte());
                Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                Assert.Throws<NotSupportedException>(() => stream.WriteByte(99));
                Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
            }
            using (var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Equal(bytes.Length, writable.Length);
            Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LeaveOpenPreservesCallerOwnership()
    {
        using var inner = new MemoryStream([1, 2]);
        using (var stream = new MetadataReadBudgetStream(inner, leaveOpen: true)) Assert.Equal(1, stream.ReadByte());
        Assert.Equal(2, inner.ReadByte());
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
    private sealed class SlowStream(Action onRead) : MemoryStream(new byte[] { 1, 2 })
    {
        public override int Read(Span<byte> buffer) { var read = base.Read(buffer); onRead(); return read; }
    }
}
