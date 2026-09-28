using System.Collections.Concurrent;
using PhotoShelf.Application.Catalog;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class LibraryChangeCoordinatorTests
{
    private static LibraryMonitorOptions FastOptions => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(10), Debounce = TimeSpan.FromMilliseconds(50),
        RootProbeInterval = TimeSpan.FromMilliseconds(50), ReconciliationInterval = TimeSpan.FromHours(1),
        RetryDelay = TimeSpan.FromMilliseconds(25), MaximumRetryDelay = TimeSpan.FromMilliseconds(100)
    };

    [Fact]
    public async Task BurstIsDeduplicatedAndQueueOverflowRequiresReconciliation()
    {
        var root = RootPath(); var factory = new FakeFactory(); var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = 0;
        await using var monitor = new LibraryChangeCoordinator(async (batch, _) =>
        {
            batches.Enqueue(batch);
            if (Interlocked.CompareExchange(ref blocked, 1, 0) == 0) { entered.TrySetResult(); await block.Task; }
        }, FastOptions with { MaximumPendingPaths = 8, MaximumPathsPerBatch = 3 }, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var initial = factory.Latest(root);
        for (var i = 0; i < 5000; i++) initial.Change(new(Path.Combine(root, $"photo-{i % 100}.jpg")));
        Assert.Equal(8, monitor.PendingPathCount);
        block.TrySetResult();
        await Until(() => batches.Count >= 2 && batches.Skip(1).Any(batch => batch.ReconcileRoots.Contains(root)));
        await Until(() => monitor.PendingPathCount == 0 && batches.SelectMany(batch => batch.ChangedPaths).Count() == 8);
        Assert.All(batches, batch => Assert.True(batch.ChangedPaths.Count <= 3));
        Assert.Equal(8, batches.SelectMany(batch => batch.ChangedPaths).Distinct().Count());
        Assert.Contains(batches.Skip(1), batch => batch.RevalidateContentRoots?.Contains(root) == true);
    }

    [Fact]
    public async Task RenameIncludesOldAndNewPathsAndIgnoresInternalDirectories()
    {
        var root = RootPath(); var cache = Path.Combine(root, "cache"); var factory = new FakeFactory();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root], [cache]));
        await Until(() => factory.Count == 1);
        var watcher = factory.Latest(root);
        watcher.Change(new(Path.Combine(root, "new.jpg"), Path.Combine(root, "old.jpg")));
        watcher.Change(new(Path.Combine(cache, "private.jpg")));
        watcher.Change(new(Path.Combine(root, ".photoshelf-moving-123", "private.jpg"), RequiresReconciliation: true));
        await Until(() => batches.SelectMany(batch => batch.ChangedPaths).Count() == 2);
        Assert.Equal(new[] { Path.Combine(root, "new.jpg"), Path.Combine(root, "old.jpg") }, batches.SelectMany(batch => batch.ChangedPaths).Order().ToArray());
    }

    [Fact]
    public async Task PauseWaitsForActualSynchronousWorkAndResumeReconciles()
    {
        var root = RootPath(); var factory = new FakeFactory();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = 0; var count = 0;
        await using var monitor = new LibraryChangeCoordinator((batch, _) =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10)); // Models synchronous FileInfo/native I/O ignoring cancellation.
                Interlocked.Exchange(ref completed, 1);
            }
            return Task.CompletedTask;
        }, FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pause = monitor.PauseAsync();
        try
        {
            await Task.Delay(75);
            Assert.False(pause.IsCompleted);
            Assert.Equal(0, completed);
        }
        finally { release.Set(); }
        await pause.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, completed);
        Assert.True(factory.Latest(root).Disposed);
        var pausedCount = count;
        factory.Latest(root).Change(new(Path.Combine(root, "stale.jpg")));
        await Task.Delay(75);
        Assert.Equal(pausedCount, count);
        await monitor.ResumeAsync();
        await Until(() => count > pausedCount && factory.Count == 2);
    }

    [Fact]
    public async Task ReconfigurationDrainsCancelledCallbackAndDoesNotPublishOldGenerationAfterReturn()
    {
        var first = RootPath(); var second = RootPath(); var factory = new FakeFactory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        var call = 0;
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            if (Interlocked.Increment(ref call) == 1) { entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested(); }
            batches.Enqueue(batch);
        }, FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([first])); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stale = factory.Latest(first);
        var configure = monitor.ConfigureAsync(new([second]));
        Assert.False(configure.IsCompleted);
        release.TrySetResult(); await configure;
        var generation = monitor.Generation;
        stale.Change(new(Path.Combine(first, "late.jpg")));
        await Until(() => batches.Any(batch => batch.Generation == generation));
        Assert.All(batches, batch => Assert.Equal(generation, batch.Generation));
        Assert.DoesNotContain(batches.SelectMany(batch => batch.ReconcileRoots), path => path == first);
        Assert.True(stale.Disposed);
    }

    [Fact]
    public async Task AddingBrowseRootPreservesExistingWatcherAndDoesNotRescanExistingRoot()
    {
        var first = RootPath(); var second = RootPath(); var factory = new FakeFactory(); var probe = new FakeProbe();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions, factory, probe);
        await monitor.ConfigureAsync(new([first])); await Until(() => probe.Calls >= 2 && batches.Any(batch => batch.ReconcileRoots.Contains(first)));
        var original = factory.Latest(first);
        await monitor.ConfigureAsync(new([first, second])); var generation = monitor.Generation;
        await Until(() => batches.Any(batch => batch.Generation == generation && batch.ReconcileRoots.Contains(second)));
        Assert.False(original.Disposed);
        Assert.DoesNotContain(batches.Where(batch => batch.Generation == generation).SelectMany(batch => batch.ReconcileRoots), path => path == first);
        original.Change(new(Path.Combine(first, "after.jpg")));
        await Until(() => batches.Any(batch => batch.Generation == generation && batch.ChangedPaths.Contains(Path.Combine(first, "after.jpg"))));
    }

    [Fact]
    public async Task OfflineRootIsRetainedAndAutomaticallyReconnected()
    {
        var root = RootPath(); var factory = new FakeFactory(); var probe = new FakeProbe { Availability = FileAvailability.RootOffline };
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions, factory, probe);
        await monitor.ConfigureAsync(new([root]));
        await Until(() => batches.SelectMany(batch => batch.RootStates).Any(state => state.Availability == FileAvailability.RootOffline));
        Assert.Equal(0, factory.Count);
        probe.Availability = FileAvailability.Available;
        await Until(() => batches.SelectMany(batch => batch.RootStates).Any(state => state.IsWatching));
        Assert.Equal(1, factory.Count);
        Assert.Contains(batches, batch => batch.RevalidateContentRoots?.Contains(root) == true);
        probe.Availability = FileAvailability.AccessDenied;
        await Until(() => factory.Latest(root).Disposed);
        Assert.Contains(batches.SelectMany(batch => batch.RootStates), state => state.Availability == FileAvailability.AccessDenied);
    }

    [Fact]
    public async Task SameSnapshotAfterOfflineGapStillRequiresContentRevalidation()
    {
        var root = RootPath(); var factory = new FakeFactory(); var probe = new FakeProbe();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions, factory, probe);
        await monitor.ConfigureAsync(new([root]));
        await Until(() => batches.Any(batch => batch.RootStates.Any(state => state.IsWatching)));
        Assert.DoesNotContain(batches, batch => batch.RevalidateContentRoots?.Contains(root) == true);
        probe.Availability = FileAvailability.RootOffline;
        await Until(() => batches.Any(batch => batch.RootStates.Any(state => state.Availability == FileAvailability.RootOffline)));
        // FakeProbe returns the same size/mtime/identity snapshot. A byte edit during the gap
        // cannot be ruled out by those fields, so reconnect must explicitly invalidate content.
        probe.Availability = FileAvailability.Available;
        await Until(() => batches.Any(batch => batch.RevalidateContentRoots?.Contains(root) == true));
        Assert.Equal(2, factory.Count);
    }

    [Fact]
    public async Task WatcherLimitAndFailureUsePeriodicFallbackAndOverflowRecovers()
    {
        var first = RootPath(); var second = RootPath(); var factory = new FakeFactory();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions with { MaximumWatchers = 1, ReconciliationInterval = TimeSpan.FromMilliseconds(100) }, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([first, second]));
        await Until(() => batches.SelectMany(batch => batch.RootStates).Any(state => state.ErrorCode == "WatcherLimitPeriodicFallback"));
        Assert.Equal(1, factory.Count);
        await Until(() => batches.SelectMany(batch => batch.ReconcileRoots).Count(path => path == second) >= 2);
        factory.Latest(first).Error(new InternalBufferOverflowException());
        await Until(() => factory.Count == 2);
        Assert.True(factory.Items.First().Disposed);
        await Until(() => batches.Any(batch => batch.RevalidateContentRoots?.Contains(first) == true));
        Assert.Contains(batches.SelectMany(batch => batch.ReconcileRoots), path => path == first);
    }

    [Fact]
    public async Task ConsumerFailureRetriesInsteadOfAcknowledgingLostChanges()
    {
        var root = RootPath(); var factory = new FakeFactory(); var calls = 0;
        var recovered = new TaskCompletionSource<LibraryMonitorBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new LibraryChangeCoordinator((batch, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("Synthetic catalog busy");
            recovered.TrySetResult(batch); return Task.CompletedTask;
        }, FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root]));
        var result = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(root, result.ReconcileRoots);
    }

    [Fact]
    public async Task FailureToCreateWatcherHasBoundedRetryAndRecoversAutomatically()
    {
        var root = RootPath(); var factory = new FakeFactory { FailAttempts = 2 };
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root]));
        await Until(() => batches.SelectMany(batch => batch.RootStates).Any(state => state.ErrorCode == "WatcherFailedPeriodicFallback"));
        await Until(() => factory.Count == 1);
        Assert.Equal(3, factory.Attempts);
        Assert.Contains(batches.SelectMany(batch => batch.ReconcileRoots), path => path == root);
    }

    [Fact]
    public async Task PauseAlsoWaitsForSynchronousRootProbeAndDoesNotStartWatcherAfterCancellation()
    {
        var root = RootPath(); var factory = new FakeFactory(); using var probe = new BlockingProbe();
        await using var monitor = new LibraryChangeCoordinator((_, _) => Task.CompletedTask, FastOptions, factory, probe);
        await monitor.ConfigureAsync(new([root]));
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pause = monitor.PauseAsync();
        try { await Task.Delay(60); Assert.False(pause.IsCompleted); }
        finally { probe.Release.Set(); }
        await pause.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, factory.Count);
    }

    [Fact]
    public async Task ContinuousEventsInOneRootDoNotStarveAnotherRoot()
    {
        var first = RootPath(); var second = RootPath(); var factory = new FakeFactory();
        var reconciled = new ConcurrentQueue<string>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) =>
        {
            foreach (var root in batch.ReconcileRoots) reconciled.Enqueue(root);
            if (batch.ReconcileRoots.Contains(first)) factory.Latest(first).Change(new(Path.Combine(first, "directory"), RequiresReconciliation: true));
            return Task.CompletedTask;
        }, FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([first, second]));
        await Until(() => reconciled.Contains(second));
        Assert.Equal(new[] { first, second }, reconciled.Take(2).ToArray());
    }

    [Fact]
    public async Task CancelledPathBatchRetainsForcedContentVerificationOnResume()
    {
        var root = RootPath(); var factory = new FakeFactory();
        var pathEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            if (batch.ChangedPaths.Count > 0)
            { pathEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            batches.Enqueue(batch);
        }, FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root]));
        await Until(() => factory.Count == 1);
        factory.Latest(root).Change(new(Path.Combine(root, "edited-preserving-timestamp.jpg")));
        await pathEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.PauseAsync();
        await monitor.ResumeAsync();
        await Until(() => batches.Any(batch => batch.RevalidateContentRoots?.Contains(root) == true));
    }

    [Fact]
    public async Task ConfigurationChangeCannotResumeExplicitPauseDuringFileMove()
    {
        var first = RootPath(); var second = RootPath(); var factory = new FakeFactory(); var calls = 0;
        await using var monitor = new LibraryChangeCoordinator((_, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; },
            FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([first])); await Until(() => calls > 0);
        await monitor.PauseAsync();
        var pausedCalls = calls;
        await monitor.ConfigureAsync(new([second]));
        await Task.Delay(100);
        Assert.Equal(pausedCalls, calls);
        Assert.Equal(1, factory.Count);
        await monitor.ResumeAsync();
        await Until(() => calls > pausedCalls && factory.Count == 2);
    }

    [Fact]
    public async Task RealWatcherNotifiesForSyntheticFileWithoutChangingOriginalBytes()
    {
        var root = RootPath(); Directory.CreateDirectory(root);
        try
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var path = Path.Combine(root, "synthetic.jpg");
            await using var monitor = new LibraryChangeCoordinator((batch, _) =>
            {
                if (batch.RootStates.Any(state => state.IsWatching)) ready.TrySetResult();
                if (batch.ChangedPaths.Contains(path)) observed.TrySetResult();
                return Task.CompletedTask;
            }, FastOptions);
            await monitor.ConfigureAsync(new([root]));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var content = new byte[] { 12, 25, 78, 99 };
            await File.WriteAllBytesAsync(path, content);
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await monitor.PauseAsync();
            Assert.Equal(content, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TooManyRootsFailExplicitlyInsteadOfSilentlyTruncating()
    {
        await using var monitor = new LibraryChangeCoordinator((_, _) => Task.CompletedTask,
            FastOptions with { MaximumRoots = 1 }, new FakeFactory(), new FakeProbe());
        await Assert.ThrowsAsync<ArgumentException>(() => monitor.ConfigureAsync(new([RootPath(), RootPath()])));
    }

    internal static string RootPath() => Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "photoshelf-monitor-" + Guid.NewGuid().ToString("N"));
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeProbe : IFileSystemObservationProbe
    {
        public volatile FileAvailability Availability = FileAvailability.Available;
        public int Calls;
        public FileSystemProbeResult ProbeFile(string path) => new(path, Availability, DateTime.UtcNow);
        public FileSystemProbeResult ProbeRoot(string path) { Interlocked.Increment(ref Calls); return new(path, Availability, DateTime.UtcNow, IsDirectory: true); }
    }

    private sealed class FakeFactory : ILibraryWatcherFactory
    {
        public ConcurrentQueue<FakeWatcher> Items { get; } = new();
        public int Count => Items.Count;
        public int FailAttempts, Attempts;
        public FakeWatcher Latest(string root) => Items.Last(item => item.Root == root);
        public IDisposable Watch(string root, bool includeSubdirectories, Action<LibraryWatchEvent> onChange, Action<Exception> onError)
        {
            if (Interlocked.Increment(ref Attempts) <= FailAttempts) throw new IOException("Synthetic watcher setup failure");
            var watcher = new FakeWatcher(root, onChange, onError); Items.Enqueue(watcher); return watcher;
        }
    }

    private sealed class BlockingProbe : IFileSystemObservationProbe, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public FileSystemProbeResult ProbeFile(string path) => ProbeRoot(path);
        public FileSystemProbeResult ProbeRoot(string path)
        {
            Entered.TrySetResult(); Release.Wait(TimeSpan.FromSeconds(10));
            return new(path, FileAvailability.Available, DateTime.UtcNow, IsDirectory: true);
        }
        public void Dispose() => Release.Dispose();
    }
    private sealed class FakeWatcher(string root, Action<LibraryWatchEvent> change, Action<Exception> error) : IDisposable
    {
        public string Root { get; } = root;
        public Action<LibraryWatchEvent> Change { get; } = change;
        public Action<Exception> Error { get; } = error;
        public volatile bool Disposed;
        public void Dispose() => Disposed = true;
    }
}
