using PhotoShelf.Application.Background;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class BackgroundWorkTests
{
    [Fact]
    public async Task VisibleWorkRunsBeforeQueuedHashWorkAndQueueCancellationDoesNotRunDelegate()
    {
        await using var scheduler = new BackgroundWorkScheduler(1, 8);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = scheduler.RunAsync(BackgroundWorkPriority.Scan, async token => { started.SetResult(); await release.Task.WaitAsync(token); return 0; });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var order = new List<string>();
        var hash = scheduler.RunAsync(BackgroundWorkPriority.Hash, _ => { order.Add("hash"); return 1; });
        using var cancellation = new CancellationTokenSource();
        var cancelled = scheduler.RunAsync(BackgroundWorkPriority.VisiblePreview, _ => { order.Add("cancelled"); return 1; }, cancellation.Token);
        var visible = scheduler.RunAsync(BackgroundWorkPriority.VisiblePreview, _ => { order.Add("visible"); return 1; });
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(2, scheduler.PendingCount);
        release.SetResult();
        await Task.WhenAll(active, hash, visible).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "visible", "hash" }, order);
    }

    [Fact]
    public async Task AdmissionIsBoundedAndCancellationReleasesWaitingCaller()
    {
        await using var scheduler = new BackgroundWorkScheduler(1, 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = scheduler.RunAsync(BackgroundWorkPriority.Scan, async token => { started.SetResult(); await release.Task.WaitAsync(token); return 0; });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = scheduler.RunAsync(BackgroundWorkPriority.Hash, _ => 1);
        using var cancellation = new CancellationTokenSource();
        var waiting = scheduler.RunAsync<int>(BackgroundWorkPriority.Hash, (Func<CancellationToken, int>)(_ => throw new InvalidOperationException("Must never run")), cancellation.Token);
        Assert.Equal(1, scheduler.PendingCount);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult();
        await Task.WhenAll(active, pending).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ShutdownCancelsRunningQueuedAndAdmissionWaitingWork()
    {
        var scheduler = new BackgroundWorkScheduler(1, 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = scheduler.RunAsync(BackgroundWorkPriority.Scan, async token => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return 0; });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = scheduler.RunAsync(BackgroundWorkPriority.Hash, _ => 1);
        var waiting = scheduler.RunAsync(BackgroundWorkPriority.Hash, _ => 2);
        await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var task in new[] { active, pending, waiting }) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task FaultDoesNotStopWorkersAndConcurrencyIsBounded()
    {
        await using var scheduler = new BackgroundWorkScheduler(2, 4);
        var failure = scheduler.RunAsync<int>(BackgroundWorkPriority.Interactive, (Func<CancellationToken, int>)(_ => throw new IOException("decode failed")));
        await Assert.ThrowsAsync<IOException>(() => failure);
        var running = 0;
        var maximum = 0;
        var tasks = Enumerable.Range(0, 40).Select(i => scheduler.RunAsync(BackgroundWorkPriority.VisiblePreview, async token =>
        {
            var count = Interlocked.Increment(ref running);
            int current;
            do { current = maximum; } while (current < count && Interlocked.CompareExchange(ref maximum, count, current) != current);
            await Task.Yield();
            Interlocked.Decrement(ref running);
            return i;
        }));
        Assert.Equal(40, (await Task.WhenAll(tasks)).Length);
        Assert.InRange(maximum, 1, 2);
    }

    [Fact]
    public async Task FullBackgroundAdmissionLeavesRoomForVisibleRequest()
    {
        await using var scheduler = new BackgroundWorkScheduler(1, 4);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = scheduler.RunAsync(BackgroundWorkPriority.Scan, async token => { started.SetResult(); await release.Task.WaitAsync(token); return 0; });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var order = new List<string>();
        var hashes = Enumerable.Range(0, 4).Select(i => scheduler.RunAsync(BackgroundWorkPriority.Hash, _ => { order.Add("hash"); return i; })).ToArray();
        Assert.Equal(3, scheduler.PendingCount);
        var visible = scheduler.RunAsync(BackgroundWorkPriority.VisiblePreview, _ => { order.Add("visible"); return 0; });
        Assert.Equal(4, scheduler.PendingCount);
        release.SetResult();
        await Task.WhenAll(hashes.Append(active).Append(visible)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("visible", order[0]);
    }

    [Fact]
    public async Task VisibleWorkStartsWhileBackgroundExecutionSlotsAreOccupied()
    {
        await using var scheduler = new BackgroundWorkScheduler(3, 16);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeBackground = 0;
        var background = Enumerable.Range(0, 6).Select(i => scheduler.RunAsync(BackgroundWorkPriority.Hash, async token =>
        {
            if (Interlocked.Increment(ref activeBackground) == 2) started.TrySetResult();
            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref activeBackground);
            return i;
        })).ToArray();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var visible = scheduler.RunAsync(BackgroundWorkPriority.VisiblePreview, _ => 42);
            Assert.Equal(42, await visible.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, Volatile.Read(ref activeBackground));
            Assert.Equal(4, scheduler.PendingCount);
            Assert.False(release.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(background).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancellingRunningReaderDoesNotCompleteItsTaskBeforeStreamIsClosed()
    {
        await using var scheduler = new BackgroundWorkScheduler(2, 4);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = Path.Combine(Path.GetTempPath(), "photoshelf-reader-test-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, "synthetic test data");
        try
        {
            var reader = scheduler.RunAsync(BackgroundWorkPriority.VisiblePreview, async _ =>
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                started.SetResult();
                await release.Task; // A synchronous codec cannot observe cancellation until it returns.
                return stream.Length;
            }, cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(reader.IsCompleted);
            release.SetResult();
            Assert.True(await reader.WaitAsync(TimeSpan.FromSeconds(5)) > 0);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(exclusive.CanWrite);
        }
        finally { release.TrySetResult(); File.Delete(path); }
    }

    [Fact]
    public void ReservationsCannotExceedSharedBudgetAndDisposeIsIdempotent()
    {
        var budget = new MemoryBudget(100);
        using var first = budget.TryReserve(70)!;
        Assert.NotNull(first);
        Assert.Null(budget.TryReserve(31));
        using var second = budget.TryReserve(30)!;
        Assert.Equal(100, budget.Used);
        Assert.False(first.TryResize(71));
        Assert.True(first.TryResize(10));
        Assert.Equal(40, budget.Used);
        second.Dispose(); second.Dispose();
        Assert.Equal(10, budget.Used);
        first.Dispose();
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public void ParallelReservationsNeverOversubscribeOrLeak()
    {
        var budget = new MemoryBudget(100);
        Parallel.For(0, 1000, _ =>
        {
            using var lease = budget.TryReserve(10);
            Assert.InRange(budget.Used, 0, 100);
        });
        Assert.Equal(0, budget.Used);
    }
}
