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
    public async Task ForegroundBrowseConsumesExactInitialPassWithoutDuplicatePublicScanMarkers()
    {
        var root = RootPath(); var factory = new FakeFactory();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) =>
        { batches.Enqueue(batch); return Task.CompletedTask; }, FastOptions with { Debounce = TimeSpan.Zero }, factory, new FakeProbe());
        var request = await monitor.ConfigureAndBrowseAsync(new([root]), new(root, true));
        await request.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var sentinel = Path.Combine(root, "after-browse.jpg");
        factory.Latest(root).Change(new(sentinel));
        await Until(() => batches.Any(batch => batch.ChangedPaths.Contains(sentinel)));
        Assert.Single(batches, batch => batch.BrowseTarget is not null);
        Assert.Empty(batches.SelectMany(batch => batch.ReconcileRoots));
        Assert.Empty(batches.SelectMany(batch => batch.DirectoryChanges ?? []));
        Assert.Equal(1, monitor.Activity.CompletedReconciliationCount);
    }

    [Fact]
    public async Task BackgroundResumeDoesNotCancelOrReplayAnAlreadyRunningPausedBrowse()
    {
        var root = RootPath();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            batches.Enqueue(batch);
            if (batch.BrowseTarget is not null) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        }, FastOptions, new FakeFactory(), new FakeProbe());
        await monitor.PauseAsync();
        var request = await monitor.ConfigureAndBrowseAsync(new([root]), new(root, true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var generation = monitor.Generation;
        await monitor.ResumeAsync(reconcileAllRoots: false);
        Assert.True(monitor.Generation > generation);
        var joined = monitor.ReconcileAsync([root]);
        Assert.Equal(0, monitor.Activity.PendingReconciliationRootCount);
        release.TrySetResult();
        await Task.WhenAll(request.Completion, joined).WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.PauseAsync();
        Assert.Single(batches, batch => batch.BrowseTarget is not null);
        Assert.Empty(batches.SelectMany(batch => batch.ReconcileRoots));
        Assert.Equal(1, monitor.Activity.CompletedReconciliationCount);
    }

    [Fact]
    public async Task DirectBrowseUnderPausedRecursiveOwnerDoesNotAcknowledgeOrProbeTheWholeRoot()
    {
        var root = RootPath(); var child = Path.Combine(root, "selected"); var probe = new FakeProbe();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) =>
        { batches.Enqueue(batch); return Task.CompletedTask; }, FastOptions, new FakeFactory(), probe);
        await monitor.PauseAsync();
        await monitor.ConfigureAsync(new([root]));
        using var cancellation = new CancellationTokenSource();
        var fullPass = monitor.ReconcileAsync([root], cancellation.Token);
        await monitor.BrowseAsync(new(child, false)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(monitor.Activity.IsPaused);
        Assert.False(fullPass.IsCompleted);
        Assert.Equal(1, monitor.Activity.PendingReconciliationRootCount);
        Assert.Equal(0, monitor.Activity.CompletedReconciliationCount);
        Assert.Equal(0, probe.Calls);
        var batch = Assert.Single(batches);
        Assert.Equal(new LibraryBrowseTarget(child, false), batch.BrowseTarget);
        Assert.Empty(batch.ReconcileRoots); Assert.Empty(batch.DirectoryChanges ?? []);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fullPass);
    }

    [Fact]
    public async Task PausedBrowsePreparesOnlyItsExactRootAndLeavesUnrelatedRootUntouched()
    {
        var root = RootPath(); var unrelated = RootPath(); var probe = new FakeProbe(); var factory = new FakeFactory();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) =>
        { batches.Enqueue(batch); return Task.CompletedTask; }, FastOptions, factory, probe);
        await monitor.PauseAsync();
        var request = await monitor.ConfigureAndBrowseAsync(new([root, unrelated]), new(root, true));
        await request.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([root], probe.Roots);
        Assert.Equal([root], factory.Items.Select(watcher => watcher.Root));
        Assert.Equal(1, monitor.Activity.PendingReconciliationRootCount);
        Assert.True(monitor.Activity.IsPaused);
        var batch = Assert.Single(batches);
        Assert.Equal(new LibraryBrowseTarget(root, true), batch.BrowseTarget);
        Assert.Equal(root, Assert.Single(batch.RootStates).Path);
        Assert.Empty(batch.ReconcileRoots);
    }

    [Fact]
    public async Task LatestBrowseCancelsOldRequestButWaitsForItsRealCallbackToRelease()
    {
        var first = RootPath(); var second = RootPath();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browses = new ConcurrentQueue<string>();
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            if (batch.BrowseTarget is not { } browse) return;
            browses.Enqueue(browse.Path);
            if (browse.Path != first) return;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); await release.Task; }
        }, FastOptions, new FakeFactory(), new FakeProbe());
        await monitor.PauseAsync();
        var request = await monitor.ConfigureAndBrowseAsync(new([first, second]), new(first, true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var next = monitor.BrowseAsync(new(second, false));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(request.Completion.IsCompleted);
            Assert.False(next.IsCompleted);
            Assert.Equal([first], browses);
            Assert.Equal(new LibraryBrowseTarget(first, true), monitor.Activity.BrowseTarget);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.Completion);
            await next.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([first, second], browses);
            Assert.True(monitor.Activity.IsPaused);
            // The interrupted first root is still pending; it was not certified by the second folder.
            Assert.True(monitor.Activity.PendingReconciliationRootCount >= 1);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForegroundCancellationOrPauseCompletesOnlyAfterActualReaderDrain(bool pause)
    {
        var root = RootPath();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browses = 0;
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            if (batch.BrowseTarget is null) return;
            Interlocked.Increment(ref browses); entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); await release.Task; }
        }, FastOptions, new FakeFactory(), new FakeProbe());
        await monitor.PauseAsync();
        using var cancellation = new CancellationTokenSource();
        var request = await monitor.ConfigureAndBrowseAsync(new([root]), new(root, true), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Task stopped = Task.CompletedTask;
            if (pause) stopped = monitor.PauseAsync();
            else cancellation.Cancel();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(request.Completion.IsCompleted);
            if (pause) Assert.False(stopped.IsCompleted);
            Assert.True(monitor.Activity.IsProcessing);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.Completion);
            await stopped.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, monitor.Activity.PendingReconciliationRootCount);
            Assert.Equal(0, monitor.Activity.CompletedReconciliationCount);
            await monitor.ResumeAsync(reconcileAllRoots: false);
            await Until(() => monitor.Activity.CompletedReconciliationCount == 1);
            Assert.Equal(1, browses); // Resume replays background intent, never the cancelled browse.
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task IncidentalConfigurationRestartsValidBrowseAttemptWithoutCancellingItsRequest()
    {
        var root = RootPath(); var other = RootPath(); var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            if (batch.BrowseTarget is null || Interlocked.Increment(ref calls) != 1) return;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); await release.Task; }
        }, FastOptions, new FakeFactory(), new FakeProbe());
        await monitor.PauseAsync();
        var request = await monitor.ConfigureAndBrowseAsync(new([root]), new(root, true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var configuration = monitor.ConfigureAsync(new([root, other]));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(request.Completion.IsCompleted); Assert.False(configuration.IsCompleted);
            release.TrySetResult();
            await configuration.WaitAsync(TimeSpan.FromSeconds(5));
            await request.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, calls);
            Assert.True(monitor.Activity.IsPaused);
            Assert.Equal(1, monitor.Activity.CompletedReconciliationCount);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task RemovingBrowseCoverageCancelsRequestAfterItsCallbackDrains()
    {
        var root = RootPath(); var other = RootPath();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            if (batch.BrowseTarget?.Path != root) return;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); await release.Task; }
        }, FastOptions, new FakeFactory(), new FakeProbe());
        await monitor.PauseAsync();
        var request = await monitor.ConfigureAndBrowseAsync(new([root]), new(root, true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var configuration = monitor.ConfigureAsync(new([other]));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(request.Completion.IsCompleted);
            release.TrySetResult();
            await configuration.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.Completion);
            await monitor.BrowseAsync(new(other, true)).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ForegroundFailureFaultsItsTicketAndLeavesConsumedBackgroundIntentPending()
    {
        var root = RootPath(); var calls = 0;
        await using var monitor = new LibraryChangeCoordinator((batch, _) =>
        {
            if (batch.BrowseTarget is not null && Interlocked.Increment(ref calls) == 1)
                throw new IOException("Synthetic browse failure");
            return Task.CompletedTask;
        }, FastOptions, new FakeFactory(), new FakeProbe());
        await monitor.PauseAsync();
        var request = await monitor.ConfigureAndBrowseAsync(new([root]), new(root, true));
        await Assert.ThrowsAsync<IOException>(() => request.Completion);
        Assert.Equal(1, monitor.Activity.PendingReconciliationRootCount);
        Assert.Equal(0, monitor.Activity.CompletedReconciliationCount);
        await monitor.BrowseAsync(new(root, true)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
        Assert.Equal(1, monitor.Activity.CompletedReconciliationCount);
    }

    [Fact]
    public async Task EventsArrivingDuringBrowseRemainPendingAndForcedContentIntentSurvivesNarrowBrowse()
    {
        var root = RootPath(); var child = Path.Combine(root, "selected"); var factory = new FakeFactory();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            batches.Enqueue(batch);
            if (batch.BrowseTarget is not null) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        }, FastOptions with { Debounce = TimeSpan.Zero }, factory, new FakeProbe());
        var initial = await monitor.ConfigureAndReconcileAsync(new([root]), [root]);
        await initial.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.PauseAsync();
        factory.Latest(root).Error(new InternalBufferOverflowException());
        var request = monitor.BrowseAsync(new(child, false));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var changed = Path.Combine(child, "same-size-edited.jpg");
        factory.Latest(root).Change(new(changed));
        Assert.Equal(1, monitor.PendingPathCount);
        release.TrySetResult();
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, monitor.Activity.PendingReconciliationRootCount);
        Assert.Equal(1, monitor.PendingPathCount);
        Assert.True(monitor.Activity.IsPaused);
        Assert.Empty(Assert.Single(batches, batch => batch.BrowseTarget is not null).RevalidateContentRoots ?? []);
        await monitor.ResumeAsync(reconcileAllRoots: false);
        await Until(() => batches.Any(batch => batch.ChangedPaths.Contains(changed)) &&
            batches.Any(batch => batch.RevalidateContentRoots?.Contains(root) == true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ManualRequestJoinedToQuietBrowseSurvivesItsCancellationOrFailure(bool fail, bool childTarget)
    {
        var root = RootPath(); var selected = childTarget ? Path.Combine(root, "selected") : root;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            batches.Enqueue(batch);
            if (batch.BrowseTarget is null) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            if (fail) throw new IOException("Synthetic browse failure");
        }, FastOptions, new FakeFactory(), new FakeProbe());
        var initial = await monitor.ConfigureAndReconcileAsync(new([root]), [root]);
        await initial.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var browse = monitor.BrowseAsync(new(selected, true), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual = monitor.ReconcileAsync([selected]);
        Assert.False(manual.IsCompleted);
        Assert.Equal(0, monitor.Activity.PendingDirectoryCount);
        Assert.Equal(0, monitor.Activity.PendingReconciliationRootCount);
        if (fail) release.TrySetResult();
        else cancellation.Cancel();
        if (fail) await Assert.ThrowsAsync<IOException>(() => browse);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browse);
        await manual.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.PauseAsync();
        if (childTarget)
        {
            Assert.Single(batches.SelectMany(batch => batch.ReconcileRoots));
            Assert.Equal(selected, Assert.Single(batches.SelectMany(batch => batch.DirectoryChanges ?? [])).Path);
        }
        else Assert.Equal(2, batches.SelectMany(batch => batch.ReconcileRoots).Count());
    }

    [Fact]
    public async Task CancelledReplacementNeverStartsAfterOldBrowseFinishesDraining()
    {
        var first = RootPath(); var second = RootPath();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browses = new ConcurrentQueue<string>();
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            if (batch.BrowseTarget is not { } target) return;
            browses.Enqueue(target.Path); entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); await release.Task; }
        }, FastOptions, new FakeFactory(), new FakeProbe());
        await monitor.PauseAsync();
        var initial = await monitor.ConfigureAndBrowseAsync(new([first, second]), new(first, true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var cancellation = new CancellationTokenSource();
            var next = monitor.BrowseAsync(new(second, true), cancellation.Token);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel(); release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initial.Completion);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
            Assert.Equal([first], browses);
            Assert.True(monitor.Activity.IsPaused);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task BrowseCannotWidenAnOnlyNonrecursiveConfiguredScope()
    {
        var root = RootPath();
        await using var monitor = new LibraryChangeCoordinator((_, _) => Task.CompletedTask,
            FastOptions, new FakeFactory(), new FakeProbe());
        await monitor.PauseAsync();
        await monitor.ConfigureAsync(new([root], NonRecursiveRoots: [root]));
        await Assert.ThrowsAsync<ArgumentException>(() => monitor.BrowseAsync(new(root, true)));
        await Assert.ThrowsAsync<ArgumentException>(() => monitor.ConfigureAndBrowseAsync(new([root], NonRecursiveRoots: [root]), new(root, true)));
        await monitor.BrowseAsync(new(root, false)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(monitor.Activity.IsPaused);
    }

    [Fact]
    public async Task AtomicRequestSharesEvenAnImmediatelyCompletedInitialPass()
    {
        var root = RootPath(); var factory = new FakeFactory();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) =>
        { batches.Enqueue(batch); return Task.CompletedTask; }, FastOptions with { Debounce = TimeSpan.Zero }, factory, new FakeProbe());

        var request = await monitor.ConfigureAndReconcileAsync(new([root]), [root]);
        await request.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var sentinel = Path.Combine(root, "after-initial.jpg");
        factory.Latest(root).Change(new(sentinel));
        await Until(() => batches.Any(batch => batch.ChangedPaths.Contains(sentinel)));

        Assert.Single(batches.SelectMany(batch => batch.ReconcileRoots));
        Assert.Empty(batches.SelectMany(batch => batch.DirectoryChanges ?? []));
        Assert.Equal(1, monitor.Activity.CompletedReconciliationCount);
    }

    [Fact]
    public async Task ExplicitRequestsSharePendingAndActiveInitialPasses()
    {
        var root = RootPath(); var child = Path.Combine(root, "photos");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            batches.Enqueue(batch);
            if (batch.ReconcileRoots.Count > 0) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        }, FastOptions, new FakeFactory(), new FakeProbe());
        await monitor.PauseAsync();
        var initial = await monitor.ConfigureAndReconcileAsync(new([root]), [root]);
        var pending = monitor.ReconcileAsync([child, root, root]);
        Assert.False(initial.Completion.IsCompleted); Assert.False(pending.IsCompleted);
        Assert.Equal(1, monitor.Activity.PendingReconciliationRootCount);
        Assert.Equal(0, monitor.Activity.PendingDirectoryCount);
        await monitor.ResumeAsync(reconcileAllRoots: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var active = monitor.ReconcileAsync([child]);
        Assert.False(active.IsCompleted);
        Assert.Equal(0, monitor.Activity.PendingReconciliationRootCount);
        Assert.Equal(0, monitor.Activity.PendingDirectoryCount);
        release.TrySetResult();
        await Task.WhenAll(initial.Completion, pending, active).WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.PauseAsync();
        Assert.Single(batches.SelectMany(batch => batch.ReconcileRoots));
        Assert.Empty(batches.SelectMany(batch => batch.DirectoryChanges ?? []));
    }

    [Fact]
    public async Task EachRequestAfterCompletionRequiresANewSuccessfulPass()
    {
        var root = RootPath(); var calls = 0;
        await using var monitor = new LibraryChangeCoordinator((batch, _) =>
        {
            if (batch.ReconcileRoots.Contains(root)) Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }, FastOptions, new FakeFactory(), new FakeProbe());
        var initial = await monitor.ConfigureAndReconcileAsync(new([root]), [root]);
        await initial.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        // Request immediately from the previous completion continuation. The finished
        // batch must already be unjoinable, including before the worker's finally runs.
        for (var expected = 2; expected <= 16; expected++)
        {
            await monitor.ReconcileAsync([root]).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expected, Volatile.Read(ref calls));
        }
    }

    [Fact]
    public async Task ChildRequestAndResumeWithoutBroadReconciliationLeaveOtherRootsQuiet()
    {
        var root = RootPath(); var other = RootPath(); var child = Path.Combine(root, "photos");
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) =>
        { batches.Enqueue(batch); return Task.CompletedTask; }, FastOptions, new FakeFactory(), new FakeProbe());
        var initial = await monitor.ConfigureAndReconcileAsync(new([root, other]), [root, other]);
        await initial.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.PauseAsync();
        var baseline = batches.Count;
        var request = monitor.ReconcileAsync([child]);
        Assert.False(request.IsCompleted);
        Assert.Equal(1, monitor.Activity.PendingDirectoryCount);
        Assert.Equal(0, monitor.Activity.PendingReconciliationRootCount);
        await monitor.ResumeAsync(reconcileAllRoots: false);
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.PauseAsync();

        Assert.Empty(batches.Skip(baseline).SelectMany(batch => batch.ReconcileRoots));
        Assert.Equal([new LibraryDirectoryChange(root, child)],
            batches.Skip(baseline).SelectMany(batch => batch.DirectoryChanges ?? []));
        Assert.All(batches.Skip(baseline), batch => Assert.Empty(batch.RevalidateContentRoots ?? []));
    }

    [Fact]
    public async Task CancellingWaiterDoesNotAcknowledgePassAndPauseStillDrainsActualCallback()
    {
        var root = RootPath(); var factory = new FakeFactory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readerReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        var calls = 0;
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            batches.Enqueue(batch);
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancellationObserved.TrySetResult(); await readerReleased.Task; }
        }, FastOptions with { Debounce = TimeSpan.Zero }, factory, new FakeProbe());
        using var cancelledCaller = new CancellationTokenSource();
        var initial = await monitor.ConfigureAndReconcileAsync(new([root]), [root], cancelledCaller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            cancelledCaller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initial.Completion);
            Assert.True(monitor.Activity.IsProcessing);
            Assert.Equal(0, monitor.Activity.CompletedReconciliationCount);
            Assert.False(cancellationObserved.Task.IsCompleted); // A detached waiter does not stop the reader.

            var pause = monitor.PauseAsync();
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var changed = Path.Combine(root, "changed-during-stop.jpg");
            factory.Latest(root).Change(new(changed));
            Assert.False(pause.IsCompleted);
            readerReleased.TrySetResult();
            await pause.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(monitor.Activity.IsProcessing);
            Assert.Equal(0, monitor.Activity.CompletedReconciliationCount);
            Assert.Equal(1, monitor.Activity.PendingReconciliationRootCount);
            Assert.Equal(1, monitor.PendingPathCount);

            var resumed = monitor.ReconcileAsync([root]);
            await monitor.ResumeAsync(reconcileAllRoots: false);
            await resumed.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => batches.Skip(1).Any(batch => batch.ChangedPaths.Contains(changed)));
            Assert.Equal(1, monitor.Activity.CompletedReconciliationCount);
            Assert.All(batches, batch => Assert.Empty(batch.RevalidateContentRoots ?? []));
        }
        finally { readerReleased.TrySetResult(); }
    }

    [Fact]
    public async Task RequestWaitsForRetryAfterConsumerFailureInsteadOfReportingSuccess()
    {
        var root = RootPath(); var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            if (!batch.ReconcileRoots.Contains(root)) return;
            if (Interlocked.Increment(ref attempts) == 1) { failed.TrySetResult(); throw new IOException("Synthetic consumer error"); }
            retryEntered.TrySetResult(); await release.Task.WaitAsync(token);
        }, FastOptions, new FakeFactory(), new FakeProbe());
        var request = await monitor.ConfigureAndReconcileAsync(new([root]), [root]);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(request.Completion.IsCompleted);
        Assert.Equal(0, monitor.Activity.CompletedReconciliationCount);
        release.TrySetResult();
        await request.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, attempts);
        Assert.Equal(1, monitor.Activity.CompletedReconciliationCount);
    }

    [Fact]
    public async Task ReconfigurationTransfersRequestsToCoveringOwnerButCancelsRemovedScopes()
    {
        var root = RootPath(); var child = Path.Combine(root, "photos"); var removed = RootPath();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) =>
        { batches.Enqueue(batch); return Task.CompletedTask; }, FastOptions, new FakeFactory(), new FakeProbe());
        await monitor.PauseAsync();
        await monitor.ConfigureAsync(new([child, removed]));
        var surviving = monitor.ReconcileAsync([child]);
        var cancelled = monitor.ReconcileAsync([removed]);
        await monitor.ConfigureAsync(new([root]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(surviving.IsCompleted);
        await monitor.ResumeAsync(reconcileAllRoots: false);
        await surviving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([root], batches.SelectMany(batch => batch.ReconcileRoots));
    }

    [Fact]
    public async Task RemovingRecursiveCoverageCancelsRequestRatherThanCertifyingOnlyDirectFiles()
    {
        var root = RootPath();
        await using var monitor = new LibraryChangeCoordinator((_, _) => Task.CompletedTask,
            FastOptions, new FakeFactory(), new FakeProbe());
        await monitor.PauseAsync();
        await monitor.ConfigureAsync(new([root]));
        var request = monitor.ReconcileAsync([root]);
        await monitor.ConfigureAsync(new([root], NonRecursiveRoots: [root]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var direct = monitor.ReconcileAsync([root]);
        await monitor.ResumeAsync(reconcileAllRoots: false);
        await direct.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposeFaultsPendingRequestWithoutLeavingAnInfiniteWaiter()
    {
        var root = RootPath();
        await using var monitor = new LibraryChangeCoordinator((_, _) => Task.CompletedTask,
            FastOptions, new FakeFactory(), new FakeProbe());
        await monitor.PauseAsync();
        var request = await monitor.ConfigureAndReconcileAsync(new([root]), [root]);
        await monitor.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => request.Completion);
    }

    [Fact]
    public async Task InvalidOrUncoveredRequestsFailExplicitlyAndDoNotPauseExistingMonitoring()
    {
        var root = RootPath(); var outside = RootPath();
        await using var monitor = new LibraryChangeCoordinator((_, _) => Task.CompletedTask,
            FastOptions with { MaximumRoots = 2 }, new FakeFactory(), new FakeProbe());
        var initial = await monitor.ConfigureAndReconcileAsync(new([root]), [root]);
        await initial.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ArgumentException>(() => { _ = monitor.ReconcileAsync([outside]); });
        Assert.Throws<ArgumentException>(() => { _ = monitor.ReconcileAsync([root, outside, RootPath()]); });
        await Assert.ThrowsAsync<ArgumentException>(() => monitor.ConfigureAndReconcileAsync(new([root]), [outside]));
        Assert.False(monitor.Activity.IsPaused);
        await monitor.ReconcileAsync([root]).WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.ConfigureAsync(new([root], NonRecursiveRoots: [root]));
        Assert.Throws<ArgumentException>(() => { _ = monitor.ReconcileAsync([Path.Combine(root, "child")]); });
    }

    [Fact]
    public async Task CancelledQueuedWaiterLeavesDiscoveryIntentForTheNextCaller()
    {
        var root = RootPath();
        await using var monitor = new LibraryChangeCoordinator((_, _) => Task.CompletedTask,
            FastOptions, new FakeFactory(), new FakeProbe());
        await monitor.PauseAsync();
        using var cancellation = new CancellationTokenSource();
        var initial = await monitor.ConfigureAndReconcileAsync(new([root]), [root], cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initial.Completion);
        Assert.Equal(1, monitor.Activity.PendingReconciliationRootCount);
        var next = monitor.ReconcileAsync([root]);
        await monitor.ResumeAsync(reconcileAllRoots: false);
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, monitor.Activity.CompletedReconciliationCount);
    }

    [Fact]
    public async Task LongReconciliationWaitsAFullPeriodicIntervalAfterSuccessfulCompletion()
    {
        var root = RootPath(); var factory = new FakeFactory(); var clock = new ManualClock();
        var interval = TimeSpan.FromMinutes(30);
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            batches.Enqueue(batch);
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        }, FastOptions with { ReconciliationInterval = interval, Debounce = TimeSpan.Zero }, factory, new FakeProbe(), clock);
        await monitor.ConfigureAsync(new([root]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var active = monitor.Activity;
            Assert.True(active.IsProcessing);
            Assert.Equal([root], active.ActiveReconciliationRoots);
            Assert.Equal(0, active.CompletedReconciliationCount);
            clock.Advance(interval + TimeSpan.FromMinutes(5));
        }
        finally { release.TrySetResult(); }

        await Until(() => monitor.Activity.CompletedReconciliationCount >= 1 && !monitor.Activity.IsProcessing);
        var completedAt = clock.GetUtcNow().UtcDateTime;
        Assert.Equal(completedAt, monitor.Activity.LastReconciliationCompletedAtUtc);
        // A real path callback is a worker-turn barrier. With the old start-based deadline,
        // the already overdue root would be reconciled again before/with this notification.
        clock.Advance(TimeSpan.FromSeconds(1));
        var sentinel = Path.Combine(root, "sentinel.jpg");
        factory.Latest(root).Change(new(sentinel));
        await Until(() => batches.Any(batch => batch.ChangedPaths.Contains(sentinel)));
        Assert.Single(batches.SelectMany(batch => batch.ReconcileRoots));
        Assert.DoesNotContain(batches, batch => batch.ChangedPaths.Contains(sentinel) && batch.ReconcileRoots.Count > 0);

        clock.Advance(interval - TimeSpan.FromSeconds(1));
        await Until(() => monitor.Activity.CompletedReconciliationCount >= 2);
        Assert.Equal(2, batches.SelectMany(batch => batch.ReconcileRoots).Count());
        await monitor.PauseAsync();
        var paused = monitor.Activity;
        Assert.True(paused.IsPaused); Assert.False(paused.IsProcessing);
        await monitor.DisposeAsync();
        Assert.True(monitor.Activity.IsDisposed);
        Assert.True(monitor.Activity.IsPaused);
        Assert.Empty(monitor.Activity.ActiveReconciliationRoots);
        Assert.False(paused.IsDisposed); // Previously returned snapshots do not mutate.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletingLongReconciliationPreservesDirectoryEventsAndWatcherErrors(bool watcherError)
    {
        var root = RootPath(); var factory = new FakeFactory(); var clock = new ManualClock();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            batches.Enqueue(batch);
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        }, FastOptions with { ReconciliationInterval = TimeSpan.FromMinutes(30) }, factory, new FakeProbe(), clock);
        await monitor.ConfigureAsync(new([root]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            clock.Advance(TimeSpan.FromMinutes(35));
            var watcher = factory.Latest(root);
            if (watcherError) watcher.Error(new InternalBufferOverflowException());
            else watcher.Change(new(Path.Combine(root, "new-folder"), RequiresReconciliation: true));
            Assert.Equal(watcherError ? 1 : 0, monitor.Activity.PendingReconciliationRootCount);
            Assert.Equal(watcherError ? 0 : 1, monitor.Activity.PendingDirectoryCount);
            Assert.Equal([root], monitor.Activity.ActiveReconciliationRoots);
        }
        finally { release.TrySetResult(); }
        await Until(() => watcherError ? monitor.Activity.CompletedReconciliationCount >= 2 :
            monitor.Activity.CompletedDirectoryReconciliationCount >= 1);
        var replay = batches.Skip(1).First(batch => watcherError ? batch.ReconcileRoots.Contains(root) :
            batch.DirectoryChanges?.Any(change => change.Path == Path.Combine(root, "new-folder")) == true);
        Assert.Equal(watcherError, replay.RevalidateContentRoots?.Contains(root) == true);
        if (!watcherError) Assert.Empty(replay.ReconcileRoots);
    }

    [Fact]
    public async Task ContinuousDirectoryEventsStayTargetedWithoutRescanningTheOwnerRoot()
    {
        var root = RootPath(); var factory = new FakeFactory();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) =>
        { batches.Enqueue(batch); return Task.CompletedTask; }, FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root], ShouldObserveFilePath: IsSyntheticMedia));
        await Until(() => monitor.Activity.CompletedReconciliationCount == 1);
        var watcher = factory.Latest(root);
        for (var index = 0; index < 40; index++)
        {
            var directory = Path.Combine(root, "application-data", "noise-" + index);
            watcher.Change(new(directory, RequiresReconciliation: true));
            watcher.Change(new(Path.Combine(directory, "ignored.log")));
            await Until(() => batches.SelectMany(batch => batch.DirectoryChanges ?? []).Any(change => change.Path == directory));
        }
        Assert.Single(batches.SelectMany(batch => batch.ReconcileRoots));
        Assert.Equal(40, batches.SelectMany(batch => batch.DirectoryChanges ?? []).Count());
        Assert.All(batches.SelectMany(batch => batch.DirectoryChanges ?? []), change => Assert.Equal(root, change.OwnerRoot));
        Assert.All(batches, batch => { Assert.Empty(batch.ChangedPaths); Assert.Empty(batch.RevalidateContentRoots ?? []); });
    }

    [Fact]
    public async Task DirectoryRenameRetainsBothSubtreesAndCoalescesQueuedDescendants()
    {
        var root = RootPath(); var factory = new FakeFactory();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            batches.Enqueue(batch);
            if (batch.ChangedPaths.Count > 0) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        }, FastOptions with { Debounce = TimeSpan.Zero }, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root]));
        await Until(() => monitor.Activity.CompletedReconciliationCount == 1);
        var watcher = factory.Latest(root);
        watcher.Change(new(Path.Combine(root, "barrier.jpg")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var oldDirectory = Path.Combine(root, "before"); var newDirectory = Path.Combine(root, "after");
        try
        {
            watcher.Change(new(Path.Combine(newDirectory, "nested"), RequiresReconciliation: true));
            watcher.Change(new(newDirectory, oldDirectory, RequiresReconciliation: true));
            watcher.Change(new(Path.Combine(newDirectory, "nested", "deeper"), RequiresReconciliation: true));
            Assert.Equal(2, monitor.Activity.PendingDirectoryCount);
            Assert.Equal(0, monitor.Activity.PendingReconciliationRootCount);
        }
        finally { release.TrySetResult(); }
        await Until(() => monitor.Activity.CompletedDirectoryReconciliationCount == 2);
        Assert.Equal(new[] { oldDirectory, newDirectory }.Order(), batches.SelectMany(batch => batch.DirectoryChanges ?? []).Select(change => change.Path).Order());
        Assert.Single(batches.SelectMany(batch => batch.ReconcileRoots));
    }

    [Fact]
    public async Task DirectoryQueueOverflowFallsBackOnlyForItsOwnerWithoutInvalidatingAllContent()
    {
        var first = RootPath(); var second = RootPath(); var factory = new FakeFactory();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            batches.Enqueue(batch);
            if (batch.ChangedPaths.Count > 0) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        }, FastOptions with { Debounce = TimeSpan.Zero, MaximumPendingDirectories = 4, MaximumDirectoriesPerBatch = 2 }, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([first, second]));
        await Until(() => monitor.Activity.CompletedReconciliationCount == 2);
        var baseline = batches.Count;
        factory.Latest(first).Change(new(Path.Combine(first, "barrier.jpg")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondDirectory = Path.Combine(second, "keep-this-target");
        try
        {
            factory.Latest(second).Change(new(secondDirectory, RequiresReconciliation: true));
            for (var index = 0; index < 2000; index++)
                factory.Latest(first).Change(new(Path.Combine(first, "directory-" + index), RequiresReconciliation: true));
            Assert.Equal(1, monitor.Activity.PendingReconciliationRootCount);
            Assert.Equal(1, monitor.Activity.PendingDirectoryCount);
        }
        finally { release.TrySetResult(); }
        await Until(() => monitor.Activity.CompletedReconciliationCount == 3 && monitor.Activity.CompletedDirectoryReconciliationCount == 1);
        Assert.Equal([first], batches.Skip(baseline).SelectMany(batch => batch.ReconcileRoots));
        Assert.Equal([secondDirectory], batches.Skip(baseline).SelectMany(batch => batch.DirectoryChanges ?? []).Select(change => change.Path));
        Assert.All(batches.Skip(baseline), batch => { Assert.Empty(batch.RevalidateContentRoots ?? []); Assert.True((batch.DirectoryChanges?.Count ?? 0) <= 2); });
    }

    [Fact]
    public async Task FailedDirectoryConsumerRetriesThroughItsOwnerWithoutDroppingTheSubtree()
    {
        var root = RootPath(); var factory = new FakeFactory();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new LibraryChangeCoordinator((batch, _) =>
        {
            batches.Enqueue(batch);
            if ((batch.DirectoryChanges?.Count ?? 0) > 0) { failed.TrySetResult(); throw new IOException("Synthetic directory consumer failure"); }
            return Task.CompletedTask;
        }, FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root]));
        await Until(() => monitor.Activity.CompletedReconciliationCount == 1);
        factory.Latest(root).Change(new(Path.Combine(root, "new-directory"), RequiresReconciliation: true));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => monitor.Activity.CompletedReconciliationCount == 2);
        Assert.Contains(batches.Skip(1), batch => batch.ReconcileRoots.Contains(root));
        Assert.All(batches, batch => Assert.Empty(batch.RevalidateContentRoots ?? []));
        Assert.Equal(0, monitor.Activity.CompletedDirectoryReconciliationCount);
    }

    [Fact]
    public async Task PauseDrainsDirectoryCallbackAndReconfigurationRetainsItsDiscoveryIntent()
    {
        var root = RootPath(); var factory = new FakeFactory();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intercept = 0;
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            if ((batch.DirectoryChanges?.Count ?? 0) > 0 && Interlocked.Exchange(ref intercept, 1) == 0)
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            batches.Enqueue(batch);
        }, FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root]));
        await Until(() => monitor.Activity.CompletedReconciliationCount == 1);
        factory.Latest(root).Change(new(Path.Combine(root, "interrupted-directory"), RequiresReconciliation: true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.PauseAsync();
        Assert.True(monitor.Activity.IsPaused); Assert.False(monitor.Activity.IsProcessing);
        Assert.Equal(1, monitor.Activity.PendingDirectoryCount);
        await monitor.ConfigureAsync(new([root]));
        Assert.True(monitor.Activity.IsPaused); Assert.Equal(1, monitor.Activity.PendingDirectoryCount);
        factory.Latest(root).Change(new(Path.Combine(root, "created-while-paused"), RequiresReconciliation: true));
        Assert.Equal(2, monitor.Activity.PendingDirectoryCount);
        Assert.Single(batches); // No callbacks can run until explicit resume.
        await monitor.ResumeAsync();
        await Until(() => monitor.Activity.CompletedReconciliationCount == 2);
        Assert.Equal(0, monitor.Activity.PendingDirectoryCount);
        Assert.Equal([root], batches.Last().ReconcileRoots); // Resume's full owner pass covers both targets.
        Assert.All(batches, batch => Assert.Empty(batch.RevalidateContentRoots ?? []));
    }

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
    public async Task UnsupportedNoiseCannotFillQueueOrInvalidateMediaWhileCallbackIsDrained()
    {
        var root = RootPath(); var factory = new FakeFactory(); var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        var scopeChecks = 0;
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions with { MaximumPendingPaths = 8 }, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root], ShouldObservePath: _ => { Interlocked.Increment(ref scopeChecks); return true; },
            ShouldObserveFilePath: IsSyntheticMedia));
        await Until(() => batches.Any(batch => batch.RootStates.Any(state => state.IsWatching)));
        await monitor.PauseAsync();
        var watcher = factory.Latest(root);
        for (var index = 0; index < 5000; index++) watcher.Change(new(Path.Combine(root, $"noise-{index}.log")));
        Assert.Equal(0, monitor.PendingPathCount);
        Assert.Equal(0, scopeChecks);
        var original = Path.Combine(root, "original.jpg"); var renamed = Path.Combine(root, "renamed.jpg");
        watcher.Change(new(renamed, original));
        // Renaming away from a supported extension must still observe the old media path.
        var removed = Path.Combine(root, "removed.jpg");
        watcher.Change(new(Path.Combine(root, "renamed-to-unsupported.tmp"), removed));
        Assert.Equal(3, monitor.PendingPathCount);
        await monitor.ResumeAsync();
        await Until(() => batches.SelectMany(batch => batch.ChangedPaths).Distinct().Count() == 3);
        Assert.Equal(new[] { original, renamed, removed }.Order(), batches.SelectMany(batch => batch.ChangedPaths).Order());
        Assert.DoesNotContain(batches, batch => (batch.RevalidateContentRoots?.Count ?? 0) > 0);
    }

    [Fact]
    public async Task NonrecursiveScopeIgnoresDeepDirectoryNoiseButReconcilesImmediateDirectoryChanges()
    {
        var root = RootPath(); var factory = new FakeFactory(); var probe = new FakeProbe();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions, factory, probe);
        await monitor.ConfigureAsync(new([root], ShouldObserveFilePath: IsSyntheticMedia, NonRecursiveRoots: [root]));
        await Until(() => probe.Calls >= 2 && batches.Any(batch => batch.ReconcileRoots.Contains(root)));
        var watcher = factory.Latest(root); var baseline = batches.Count;
        Assert.False(watcher.IncludeSubdirectories);
        for (var index = 0; index < 2000; index++)
        {
            watcher.Change(new(Path.Combine(root, "child", $"nested-{index}"), RequiresReconciliation: true));
            watcher.Change(new(Path.Combine(root, "child", $"nested-{index}.jpg")));
        }
        var direct = Path.Combine(root, "direct.jpg"); watcher.Change(new(direct));
        await Until(() => batches.Skip(baseline).Any(batch => batch.ChangedPaths.Contains(direct)));
        Assert.All(batches.Skip(baseline), batch => Assert.Empty(batch.ReconcileRoots));
        Assert.Equal([direct], batches.Skip(baseline).SelectMany(batch => batch.ChangedPaths));
        var immediate = Path.Combine(root, "new-child"); watcher.Change(new(immediate, RequiresReconciliation: true));
        await Until(() => batches.Skip(baseline).Any(batch => batch.DirectoryChanges?.Any(change => change.OwnerRoot == root && change.Path == immediate) == true));
        Assert.All(batches.Skip(baseline), batch => Assert.Empty(batch.ReconcileRoots));
        Assert.DoesNotContain(batches.SelectMany(batch => batch.ChangedPaths), path => path == immediate);
        Assert.DoesNotContain(batches, batch => (batch.RevalidateContentRoots?.Count ?? 0) > 0);
    }

    [Fact]
    public async Task RecursiveLibraryAndNonrecursiveBrowseKeepIndependentNotificationScopes()
    {
        var browse = RootPath(); var library = Path.Combine(browse, "library"); var factory = new FakeFactory(); var probe = new FakeProbe();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions, factory, probe);
        await monitor.ConfigureAsync(new([browse, library], ShouldObserveFilePath: IsSyntheticMedia, NonRecursiveRoots: [browse]));
        await Until(() => probe.Calls >= 4 && batches.SelectMany(batch => batch.ReconcileRoots).Distinct().Count() == 2);
        Assert.False(factory.Latest(browse).IncludeSubdirectories); Assert.True(factory.Latest(library).IncludeSubdirectories);
        var baseline = batches.Count;
        var nested = Path.Combine(library, "child", "nested.jpg"); var direct = Path.Combine(browse, "direct.jpg");
        factory.Latest(browse).Change(new(nested));
        factory.Latest(browse).Change(new(Path.Combine(browse, "other", "ignored.jpg")));
        factory.Latest(browse).Change(new(Path.Combine(library, "child"), RequiresReconciliation: true));
        factory.Latest(library).Change(new(nested));
        factory.Latest(library).Change(new(Path.Combine(library, "child"), RequiresReconciliation: true));
        factory.Latest(browse).Change(new(direct));
        await Until(() => batches.Skip(baseline).SelectMany(batch => batch.ChangedPaths).Distinct().Count() == 2 &&
            batches.Skip(baseline).Any(batch => batch.DirectoryChanges?.Any(change => change.OwnerRoot == library && change.Path == Path.GetDirectoryName(nested)) == true));
        Assert.Equal(new[] { direct, nested }.Order(), batches.Skip(baseline).SelectMany(batch => batch.ChangedPaths).Order());
        Assert.DoesNotContain(batches.Skip(baseline).SelectMany(batch => batch.ReconcileRoots), root => root == browse);
    }

    [Fact]
    public async Task ChangingOnlyBrowseRecursionPreservesLibraryWatcherAndPendingLibraryPath()
    {
        var browse = RootPath(); var library = Path.Combine(browse, "library"); var factory = new FakeFactory(); var probe = new FakeProbe();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions, factory, probe);
        await monitor.ConfigureAsync(new([browse, library], ShouldObserveFilePath: IsSyntheticMedia));
        await Until(() => probe.Calls >= 4 && batches.SelectMany(batch => batch.ReconcileRoots).Distinct().Count() == 2);
        await monitor.PauseAsync();
        var originalBrowse = factory.Latest(browse); var originalLibrary = factory.Latest(library);
        var pending = Path.Combine(library, "child", "pending.jpg"); originalBrowse.Change(new(pending));
        await monitor.ConfigureAsync(new([browse, library], ShouldObserveFilePath: IsSyntheticMedia, NonRecursiveRoots: [browse]));
        Assert.True(originalBrowse.Disposed); Assert.False(originalLibrary.Disposed);
        Assert.Equal(1, monitor.PendingPathCount);
        await monitor.ResumeAsync(); var generation = monitor.Generation;
        await Until(() => batches.Any(batch => batch.Generation == generation && batch.ChangedPaths.Contains(pending)));
        Assert.Equal(3, factory.Count);
        Assert.False(factory.Latest(browse).IncludeSubdirectories);
        Assert.Same(originalLibrary, factory.Latest(library));
        Assert.Contains(batches, batch => batch.Generation == generation && batch.RevalidateContentRoots?.Contains(browse) == true);
        Assert.DoesNotContain(batches, batch => batch.RevalidateContentRoots?.Contains(library) == true);
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
        Assert.False(factory.Latest(root).Disposed);
        var pausedCount = count;
        factory.Latest(root).Change(new(Path.Combine(root, "queued-while-paused.jpg")));
        await Task.Delay(75);
        Assert.Equal(pausedCount, count);
        await monitor.ResumeAsync();
        await Until(() => count > pausedCount);
        Assert.Equal(1, factory.Count);
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
        // Watcher disposal precedes publishing the batch; wait for the consumer-visible state.
        await Until(() => batches.SelectMany(batch => batch.RootStates)
            .Any(state => state.Path == root && state.Availability == FileAvailability.AccessDenied));
        Assert.True(factory.Latest(root).Disposed);
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
    public async Task CancelledPathBatchReplaysTargetedVerificationWithoutInvalidatingWholeRoot()
    {
        var root = RootPath(); var factory = new FakeFactory();
        var pathEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        var pathCalls = 0;
        var changed = Path.Combine(root, "edited-preserving-timestamp.jpg");
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            if (batch.ChangedPaths.Count > 0 && Interlocked.Increment(ref pathCalls) == 1)
            { pathEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            batches.Enqueue(batch);
        }, FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root]));
        await Until(() => factory.Count == 1);
        factory.Latest(root).Change(new(changed));
        await pathEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.PauseAsync();
        await monitor.ResumeAsync();
        await Until(() => batches.Any(batch => batch.ChangedPaths.Contains(changed)));
        Assert.DoesNotContain(batches, batch => batch.RevalidateContentRoots?.Contains(root) == true);
        Assert.Equal(1, factory.Count);
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
    public async Task QuietPauseResumeKeepsWatcherAndDoesNotInvalidateUnchangedContentCaches()
    {
        var root = RootPath(); var factory = new FakeFactory();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root]));
        await Until(() => batches.Any(batch => batch.ReconcileRoots.Contains(root)));
        var watcher = factory.Latest(root);
        await monitor.PauseAsync();
        Assert.False(watcher.Disposed);
        await monitor.ResumeAsync();
        var generation = monitor.Generation;
        await Until(() => batches.Any(batch => batch.Generation == generation && batch.ReconcileRoots.Contains(root)));
        Assert.Equal(1, factory.Count);
        Assert.All(batches, batch => Assert.Empty(batch.RevalidateContentRoots ?? []));
        Assert.All(batches, batch => Assert.Empty(batch.ChangedPaths));
    }

    [Fact]
    public async Task PausedEventsSurviveBrowseReconfigurationWithoutRunningProbesOrCallbacks()
    {
        var root = RootPath(); var browse = RootPath(); var factory = new FakeFactory(); var probe = new FakeProbe();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions, factory, probe);
        await monitor.ConfigureAsync(new([root]));
        await Until(() => factory.Count == 1);
        await monitor.PauseAsync();
        var callbackCount = batches.Count; var probeCount = probe.Calls;
        var first = Path.Combine(root, "first.jpg"); var second = Path.Combine(root, "second.jpg");
        factory.Latest(root).Change(new(first)); factory.Latest(root).Change(new(second));
        await monitor.ConfigureAsync(new([root, browse]));
        await Task.Delay(100);
        Assert.Equal(2, monitor.PendingPathCount);
        Assert.Equal(probeCount, probe.Calls); Assert.Equal(callbackCount, batches.Count);
        await monitor.ResumeAsync();
        var generation = monitor.Generation;
        await Until(() => batches.Where(batch => batch.Generation == generation).SelectMany(batch => batch.ChangedPaths).Count() == 2);
        Assert.Equal(new[] { first, second }, batches.Where(batch => batch.Generation == generation)
            .SelectMany(batch => batch.ChangedPaths).Order().ToArray());
        Assert.All(batches, batch => Assert.Empty(batch.RevalidateContentRoots ?? []));
        Assert.Equal(2, factory.Count); // one retained library watcher plus the newly browsed folder
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetiringOverlappingWatcherTransfersPendingPathAndOverflowIntent(bool overflow, bool retireParent)
    {
        var library = RootPath(); var browse = Path.Combine(library, "browse");
        var retiring = retireParent ? library : browse;
        var retained = retireParent ? browse : library;
        var factory = new FakeFactory(); var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions with { MaximumPendingPaths = 2 }, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([library, browse]));
        await Until(() => factory.Count == 2);
        await monitor.PauseAsync();
        var changed = Path.Combine(browse, "same-size-edit.jpg");
        factory.Latest(retiring).Change(new(changed)); // this subscription wins deduplication
        factory.Latest(retained).Change(new(changed));
        if (overflow)
            for (var index = 0; index < 10; index++) factory.Latest(retiring).Change(new(Path.Combine(browse, $"burst-{index}.jpg")));
        await monitor.ConfigureAsync(new([retained]));
        Assert.True(monitor.PendingPathCount > 0);
        Assert.True(factory.Latest(retiring).Disposed);
        Assert.False(factory.Latest(retained).Disposed);
        await monitor.ResumeAsync();
        var generation = monitor.Generation;
        await Until(() => batches.Any(batch => batch.Generation == generation && batch.ChangedPaths.Contains(changed)));
        if (overflow)
            await Until(() => batches.Any(batch => batch.Generation == generation && batch.RevalidateContentRoots?.Contains(retained) == true));
        else Assert.DoesNotContain(batches, batch => (batch.RevalidateContentRoots?.Count ?? 0) > 0);
        Assert.DoesNotContain(batches.Where(batch => batch.Generation == generation).SelectMany(batch => batch.ReconcileRoots), path => path == retiring);
        Assert.Equal(2, factory.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiringOverlappingWatcherRetainsInterruptedDirectoryDiscovery(bool retireParent)
    {
        var parent = RootPath(); var child = Path.Combine(parent, "child");
        var retiring = retireParent ? parent : child; var retained = retireParent ? child : parent;
        var factory = new FakeFactory(); var probe = new FakeProbe(); var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intercept = 0;
        await using var monitor = new LibraryChangeCoordinator(async (batch, token) =>
        {
            if (batch.DirectoryChanges?.Any(change => change.OwnerRoot == retiring) == true && Interlocked.CompareExchange(ref intercept, 2, 1) == 1)
            { interrupted.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            batches.Enqueue(batch);
        }, FastOptions, factory, probe);
        await monitor.ConfigureAsync(new([parent, child]));
        await Until(() => probe.Calls >= 4 && batches.SelectMany(batch => batch.ReconcileRoots).Distinct().Count() == 2);
        Interlocked.Exchange(ref intercept, 1);
        factory.Latest(retiring).Change(new(Path.Combine(child, "new-directory"), RequiresReconciliation: true));
        await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.ConfigureAsync(new([retained])); // cancellation must transfer the unprocessed directory scope
        var generation = monitor.Generation;
        await Until(() => batches.Any(batch => batch.Generation == generation &&
            batch.DirectoryChanges?.Any(change => change.OwnerRoot == retained && change.Path == Path.Combine(child, "new-directory")) == true));
        Assert.DoesNotContain(batches, batch => (batch.RevalidateContentRoots?.Count ?? 0) > 0);
        Assert.DoesNotContain(batches.Where(batch => batch.Generation == generation).SelectMany(batch => batch.ReconcileRoots), path => path == retiring);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingParentSubscriptionWithNewChildDuringPauseRetainsRealNotificationGap(bool initiallyFailed)
    {
        var parent = RootPath(); var child = Path.Combine(parent, "child"); var unrelated = RootPath();
        var factory = new FakeFactory { FailAttempts = initiallyFailed ? int.MaxValue : 0 }; var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([parent])); await Until(() => batches.SelectMany(batch => batch.RootStates).Any());
        await monitor.PauseAsync();
        factory.FailAttempts = 0;
        await monitor.ConfigureAsync(new([child, unrelated]));
        if (!initiallyFailed) Assert.True(factory.Latest(parent).Disposed);
        // No watcher covers child now; its next available snapshot must not assume unchanged
        // content merely because size/mtime still match. A completely unrelated new scope
        // has no inherited overflow/gap evidence and must remain a normal reconciliation.
        await monitor.ResumeAsync();
        var generation = monitor.Generation;
        await Until(() => batches.Any(batch => batch.Generation == generation && batch.RevalidateContentRoots?.Contains(child) == true));
        Assert.DoesNotContain(batches, batch => batch.RevalidateContentRoots?.Contains(unrelated) == true);
    }

    [Fact]
    public async Task ChangingRecursionDuringPauseRetainsActualSubscriptionGap()
    {
        var root = RootPath(); var factory = new FakeFactory(); var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root], IncludeSubdirectories: false));
        await Until(() => batches.SelectMany(batch => batch.RootStates).Any(state => state.IsWatching));
        await monitor.PauseAsync();
        var previousWatcher = factory.Latest(root);
        await monitor.ConfigureAsync(new([root], IncludeSubdirectories: true));
        Assert.True(previousWatcher.Disposed);
        Assert.Equal(1, factory.Count);
        await monitor.ResumeAsync();
        var generation = monitor.Generation;
        await Until(() => batches.Any(batch => batch.Generation == generation && batch.RevalidateContentRoots?.Contains(root) == true));
        Assert.Equal(2, factory.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverflowOrWatcherErrorDuringPauseForcesOnlyAffectedRootAfterResume(bool nativeWatcherError)
    {
        var root = RootPath(); var other = RootPath(); var factory = new FakeFactory(); var probe = new FakeProbe();
        var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions with { MaximumPendingPaths = 8 }, factory, probe);
        await monitor.ConfigureAsync(new([root, other]));
        await Until(() => factory.Count == 2);
        await monitor.PauseAsync();
        var callbackCount = batches.Count; var probeCount = probe.Calls;
        if (nativeWatcherError) factory.Latest(root).Error(new InternalBufferOverflowException());
        else for (var index = 0; index < 1000; index++) factory.Latest(root).Change(new(Path.Combine(root, $"image-{index}.jpg")));
        await Task.Delay(100);
        Assert.True(monitor.PendingPathCount <= 8);
        Assert.Equal(probeCount, probe.Calls); Assert.Equal(callbackCount, batches.Count);
        await monitor.ResumeAsync();
        await Until(() => batches.Any(batch => batch.RevalidateContentRoots?.Contains(root) == true));
        Assert.DoesNotContain(batches, batch => batch.RevalidateContentRoots?.Contains(other) == true);
    }

    [Fact]
    public async Task WatcherErrorDuringLongPausePublishesHealthyStateAfterSuccessfulRestart()
    {
        var root = RootPath(); var factory = new FakeFactory(); var batches = new ConcurrentQueue<LibraryMonitorBatch>();
        await using var monitor = new LibraryChangeCoordinator((batch, _) => { batches.Enqueue(batch); return Task.CompletedTask; },
            FastOptions, factory, new FakeProbe());
        await monitor.ConfigureAsync(new([root]));
        await Until(() => batches.SelectMany(batch => batch.RootStates).Any(state => state.IsWatching));
        await monitor.PauseAsync();
        factory.Latest(root).Error(new IOException("Synthetic native watcher failure"));
        await Task.Delay(150); // The restart backoff expires while no probes/callbacks can run.
        await monitor.ResumeAsync(); var generation = monitor.Generation;
        await Until(() => batches.Where(batch => batch.Generation == generation).SelectMany(batch => batch.RootStates)
            .Any(state => state.IsWatching && state.Availability == FileAvailability.Available && state.ErrorCode is null));
        Assert.Equal(2, factory.Count);
        Assert.Contains(batches, batch => batch.Generation == generation && batch.RevalidateContentRoots?.Contains(root) == true);
    }

    [WindowsFact]
    public async Task RetainedWindowsDirectoryWatchersDoNotLockSyntheticMediaDuringPause()
    {
        var root = RootPath(); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "synthetic.jpg");
        var renamed = Path.Combine(root, "renamed.jpg");
        var bytes = new byte[] { 4, 8, 15, 16, 23, 42 };
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changes = new ConcurrentQueue<string>();
            await using var monitor = new LibraryChangeCoordinator((batch, _) =>
            {
                if (batch.RootStates.Any(state => state.IsWatching)) ready.TrySetResult();
                foreach (var changed in batch.ChangedPaths) changes.Enqueue(changed);
                return Task.CompletedTask;
            }, FastOptions);
            await monitor.ConfigureAsync(new([root])); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await monitor.PauseAsync();
            // A media-reader handle would prevent this exclusive open. Directory notification
            // handles permit it, and also permit a no-overwrite rename of the owned fixture.
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal(bytes.Length, exclusive.Length);
            }
            File.Move(path, renamed);
            await Until(() => monitor.PendingPathCount >= 2);
            Assert.Empty(changes);
            await monitor.ResumeAsync();
            await Until(() => changes.Contains(path) && changes.Contains(renamed));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(renamed));
        }
        finally { Directory.Delete(root, recursive: true); }
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
        await Assert.ThrowsAsync<ArgumentException>(() => monitor.ConfigureAsync(new([RootPath()], NonRecursiveRoots: [RootPath(), RootPath()])));
    }

    internal static string RootPath() => Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "photoshelf-monitor-" + Guid.NewGuid().ToString("N"));
    private static bool IsSyntheticMedia(string path) => Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase);
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeProbe : IFileSystemObservationProbe
    {
        public volatile FileAvailability Availability = FileAvailability.Available;
        public int Calls;
        public ConcurrentQueue<string> Roots { get; } = new();
        public FileSystemProbeResult ProbeFile(string path) => new(path, Availability, DateTime.UtcNow);
        public FileSystemProbeResult ProbeRoot(string path) { Interlocked.Increment(ref Calls); Roots.Enqueue(path); return new(path, Availability, DateTime.UtcNow, IsDirectory: true); }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
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
            var watcher = new FakeWatcher(root, includeSubdirectories, onChange, onError); Items.Enqueue(watcher); return watcher;
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
    private sealed class FakeWatcher(string root, bool includeSubdirectories, Action<LibraryWatchEvent> change, Action<Exception> error) : IDisposable
    {
        public string Root { get; } = root;
        public bool IncludeSubdirectories { get; } = includeSubdirectories;
        public Action<LibraryWatchEvent> Change { get; } = change;
        public Action<Exception> Error { get; } = error;
        public volatile bool Disposed;
        public void Dispose() => Disposed = true;
    }
}
