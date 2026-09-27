namespace PhotoShelf.Application.Background;

/// <summary>Shared accounting for retained decoded pixels and estimated scratch buffers. Reservations never exceed Capacity.</summary>
public sealed class MemoryBudget
{
    private readonly object _sync = new();
    private long _used;
    public MemoryBudget(long capacity) { ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1); Capacity = capacity; }
    public long Capacity { get; }
    public long Used { get { lock (_sync) return _used; } }
    public Lease? TryReserve(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_sync)
        {
            if (bytes > Capacity - _used) return null;
            _used += bytes;
            return new Lease(this, bytes);
        }
    }
    public sealed class Lease : IDisposable
    {
        private readonly MemoryBudget _owner;
        private bool _disposed;
        private long _bytes;
        internal Lease(MemoryBudget owner, long bytes) { _owner = owner; _bytes = bytes; }
        public long Bytes { get { lock (_owner._sync) return _bytes; } }
        public bool TryResize(long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            lock (_owner._sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Lease));
                if (bytes > _bytes && bytes - _bytes > _owner.Capacity - _owner._used) return false;
                _owner._used += bytes - _bytes;
                _bytes = bytes;
                return true;
            }
        }
        public void Dispose()
        {
            lock (_owner._sync)
            {
                if (_disposed) return;
                _owner._used -= _bytes;
                _bytes = 0;
                _disposed = true;
            }
        }
    }
}
