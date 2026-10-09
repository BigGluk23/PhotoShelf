namespace PhotoShelf.Application.Background;

public enum BackgroundWorkPriority { VisiblePreview, Interactive, Scan, Metadata, Hash, Maintenance }

/// <summary>Bounded background workers. Running jobs cooperate with cancellation; queued jobs are ordered by priority and FIFO.</summary>
public sealed class BackgroundWorkScheduler : IAsyncDisposable
{
    public static BackgroundWorkScheduler Shared { get; } = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4), 256);
    private readonly object _sync = new();
    private readonly List<WorkItem> _queue = new();
    private readonly SemaphoreSlim _available;
    private readonly int _concurrency;
    private readonly int _backgroundConcurrency;
    private readonly SemaphoreSlim _capacity;
    private readonly SemaphoreSlim _backgroundCapacity;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private long _sequence;
    private int _running;
    private int _runningBackground;
    private bool _disposed;

    public BackgroundWorkScheduler(int concurrency, int pendingCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pendingCapacity, 1);
        _concurrency = concurrency;
        _backgroundConcurrency = Math.Max(1, concurrency - 1);
        _available = new(0, concurrency);
        _capacity = new(pendingCapacity, pendingCapacity);
        var backgroundSlots = Math.Max(1, pendingCapacity * 3 / 4);
        _backgroundCapacity = new(backgroundSlots, backgroundSlots);
        _workers = Enumerable.Range(0, concurrency).Select(_ => Task.Run(WorkerAsync)).ToArray();
    }

    public int PendingCount { get { lock (_sync) return _queue.Count; } }
    public int RunningCount => Volatile.Read(ref _running);

    public Task<T> RunAsync<T>(BackgroundWorkPriority priority, Func<CancellationToken, T> work, CancellationToken cancellationToken = default, BackgroundWorkController.Activity? activity = null) =>
        RunAsync(priority, token => Task.FromResult(work(token)), cancellationToken, activity);

    public async Task<T> RunAsync<T>(BackgroundWorkPriority priority, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default, BackgroundWorkController.Activity? activity = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        activity?.SetPhase(BackgroundTaskPhase.Queued);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var background = priority >= BackgroundWorkPriority.Scan;
        if (background) await _backgroundCapacity.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await _capacity.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch { if (background) _backgroundCapacity.Release(); throw; }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(priority, Interlocked.Increment(ref _sequence), async () =>
        {
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                activity?.SetPhase(BackgroundTaskPhase.Reading);
                completion.TrySetResult(await work(linked.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { completion.TrySetCanceled(linked.Token); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }, () => completion.TrySetCanceled(linked.Token), background);
        lock (_sync)
        {
            if (_disposed || linked.IsCancellationRequested)
            {
                ReleaseSlot(item);
                linked.Token.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(BackgroundWorkScheduler));
            }
            _queue.Add(item);
        }
        using var registration = linked.Token.Register(() =>
        {
            lock (_sync)
            {
                if (!_queue.Remove(item)) return;
                ReleaseSlot(item);
                item.Cancel();
            }
        });
        lock (_sync) SignalEligibleWork();
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await _available.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                WorkItem? item;
                lock (_sync)
                {
                    _shutdown.Token.ThrowIfCancellationRequested();
                    item = _queue.Where(job => !job.IsBackground || _runningBackground < _backgroundConcurrency)
                        .MinBy(job => ((int)job.Priority, job.Sequence));
                    if (item is null) continue; // Signals are bounded by worker count, not queued-job count.
                    _queue.Remove(item);
                    ReleaseSlot(item);
                    if (item.IsBackground) _runningBackground++;
                    Interlocked.Increment(ref _running);
                    SignalEligibleWork();
                }
                try { await item.Execute().ConfigureAwait(false); }
                finally
                {
                    lock (_sync)
                    {
                        if (item.IsBackground) _runningBackground--;
                        Interlocked.Decrement(ref _running);
                        SignalEligibleWork();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    // A reserved execution slot (not only queue space) keeps previews responsive while long scans/hashes run.
    // When every background slot is occupied there are no new wakeups until a job completes or foreground arrives.
    private void SignalEligibleWork()
    {
        if (_disposed) return;
        var eligibleForeground = _queue.Count(job => !job.IsBackground);
        var eligibleBackground = Math.Min(_backgroundConcurrency - _runningBackground, _queue.Count(job => job.IsBackground));
        var wanted = Math.Min(_concurrency, eligibleForeground + eligibleBackground);
        var missing = wanted - _available.CurrentCount;
        if (missing > 0) _available.Release(missing);
    }

    private void ReleaseSlot(WorkItem item)
    {
        _capacity.Release();
        if (item.IsBackground) _backgroundCapacity.Release();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_workers).ConfigureAwait(false);
        // Semaphores stay alive so concurrent cancelled admission waiters can unwind safely.
    }

    private sealed record WorkItem(BackgroundWorkPriority Priority, long Sequence, Func<Task> Execute, Action Cancel, bool IsBackground);
}
