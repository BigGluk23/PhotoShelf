namespace PhotoShelf.Application.Catalog;

public sealed record LibraryMonitorConfiguration(IReadOnlyList<string> Roots,
    IReadOnlyList<string>? IgnoredDirectories = null, bool IncludeSubdirectories = true,
    Func<string, bool>? ShouldObservePath = null, Func<string, bool>? ShouldObserveFilePath = null,
    IReadOnlyList<string>? NonRecursiveRoots = null);

public sealed record LibraryRootState(string Path, FileAvailability Availability, bool IsWatching, string? ErrorCode = null);
public sealed record LibraryDirectoryChange(string OwnerRoot, string Path);
public sealed record LibraryMonitorBatch(long Generation, IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<string> ReconcileRoots, IReadOnlyList<LibraryRootState> RootStates,
    IReadOnlyList<string>? RevalidateContentRoots = null,
    IReadOnlyList<LibraryDirectoryChange>? DirectoryChanges = null);

public sealed record LibraryMonitorActivity(long Generation, bool IsPaused, bool IsDisposed, bool IsProcessing,
    IReadOnlyList<string> ActiveReconciliationRoots, int ActiveChangedPathCount,
    int PendingPathCount, int PendingReconciliationRootCount, long CompletedReconciliationCount,
    DateTime? LastReconciliationCompletedAtUtc, int ActiveDirectoryCount, int PendingDirectoryCount,
    long CompletedDirectoryReconciliationCount);

public sealed record LibraryMonitorOptions
{
    public int MaximumRoots { get; init; } = 4096;
    public int MaximumWatchers { get; init; } = 32;
    public int MaximumPendingPaths { get; init; } = 1024;
    public int MaximumPathsPerBatch { get; init; } = 256;
    public int MaximumPendingDirectories { get; init; } = 1024;
    public int MaximumDirectoriesPerBatch { get; init; } = 32;
    public int MaximumReconcileRootsPerBatch { get; init; } = 1;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan RootProbeInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(1);
}

public sealed record LibraryWatchEvent(string Path, string? OldPath = null, bool RequiresReconciliation = false);

public interface ILibraryWatcherFactory
{
    IDisposable Watch(string root, bool includeSubdirectories, Action<LibraryWatchEvent> onChange, Action<Exception> onError);
}

/// <summary>All filesystem I/O, watcher disposal, and callbacks execute on one worker. No original writes.</summary>
public sealed class LibraryChangeCoordinator : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly SemaphoreSlim _control = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<LibraryMonitorBatch, CancellationToken, Task> _callback;
    private readonly LibraryMonitorOptions _options;
    private readonly ILibraryWatcherFactory _factory;
    private readonly IFileSystemObservationProbe _probe;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly DirtyRootQueue _dirty = new();
    private readonly HashSet<string> _revalidateContent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Root> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _worker;
    private LibraryMonitorConfiguration _configuration = new([]);
    private CancellationTokenSource _generationCancellation = new();
    private long _generation;
    private DateTime _firstChange;
    private DateTime _callbackRetryAt;
    private bool _paused = true, _explicitlyPaused, _disposed;
    private LibraryMonitorBatch? _activeBatch;
    private long _completedReconciliations;
    private long _completedDirectoryReconciliations;
    private DateTime? _lastReconciliationCompletedAtUtc;

    public LibraryChangeCoordinator(Func<LibraryMonitorBatch, CancellationToken, Task> callback,
        LibraryMonitorOptions? options = null, ILibraryWatcherFactory? watcherFactory = null,
        IFileSystemObservationProbe? probe = null, TimeProvider? clock = null)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _options = options ?? new();
        if (_options.MaximumRoots < 1 || _options.MaximumWatchers < 0 || _options.MaximumPendingPaths < 1
            || _options.MaximumPathsPerBatch < 1 || _options.MaximumPendingDirectories < 1
            || _options.MaximumDirectoriesPerBatch < 1 || _options.MaximumReconcileRootsPerBatch < 1
            || _options.PollInterval <= TimeSpan.Zero || _options.Debounce < TimeSpan.Zero
            || _options.RootProbeInterval <= TimeSpan.Zero || _options.ReconciliationInterval <= TimeSpan.Zero
            || _options.RetryDelay <= TimeSpan.Zero || _options.MaximumRetryDelay < _options.RetryDelay)
            throw new ArgumentOutOfRangeException(nameof(options));
        _factory = watcherFactory ?? new FileSystemLibraryWatcherFactory();
        _probe = probe ?? new FileSystemObservationProbe();
        _clock = clock ?? TimeProvider.System;
        _worker = Task.Run(RunAsync);
    }

    public long Generation { get { lock (_sync) return _generation; } }
    public int PendingPathCount { get { lock (_sync) return _paths.Count; } }
    public LibraryMonitorActivity Activity
    {
        get
        {
            lock (_sync)
                return new(_generation, _paused, _disposed, _activeBatch is not null,
                    Array.AsReadOnly(_activeBatch is null ? [] : _activeBatch.ReconcileRoots
                        .Concat((_activeBatch.DirectoryChanges ?? []).Select(change => change.Path)).ToArray()),
                    _activeBatch?.ChangedPaths.Count ?? 0, _paths.Count, _dirty.Count,
                    _completedReconciliations, _lastReconciliationCompletedAtUtc,
                    _activeBatch?.DirectoryChanges?.Count ?? 0, _directories.Count, _completedDirectoryReconciliations);
        }
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public async Task ConfigureAsync(LibraryMonitorConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // Normalize strings only; configuring from the UI performs no filesystem I/O.
        var roots = configuration.Roots.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).Take(_options.MaximumRoots + 1).ToArray();
        if (roots.Length > _options.MaximumRoots)
            throw new ArgumentException($"Monitoring supports at most {_options.MaximumRoots} roots; no roots were silently discarded.", nameof(configuration));
        var ignored = (configuration.IgnoredDirectories ?? []).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).Take(_options.MaximumRoots + 1).ToArray();
        if (ignored.Length > _options.MaximumRoots) throw new ArgumentException("Too many ignored directories.", nameof(configuration));
        var nonRecursive = (configuration.NonRecursiveRoots ?? []).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).Take(_options.MaximumRoots + 1).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (nonRecursive.Count > _options.MaximumRoots) throw new ArgumentException("Too many nonrecursive roots.", nameof(configuration));
        bool Recurse(string path) => configuration.IncludeSubdirectories && !nonRecursive.Contains(path);
        await _control.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await PauseCoreAsync(closeWatchers: false).ConfigureAwait(false);
            List<IDisposable> retired = [];
            lock (_sync)
            {
                _generation++;
                var filtersChanged = _configuration.ShouldObservePath != configuration.ShouldObservePath
                    || _configuration.ShouldObserveFilePath != configuration.ShouldObserveFilePath
                    || !(_configuration.IgnoredDirectories ?? []).SequenceEqual(ignored, StringComparer.OrdinalIgnoreCase);
                _configuration = configuration with { Roots = roots, IgnoredDirectories = ignored, NonRecursiveRoots = nonRecursive.ToArray() };
                var requested = roots.Where(path => !IsIgnored(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var uncoveredReplacements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in _roots.Values.ToArray())
                {
                    if (!requested.Contains(root.Path))
                    {
                        if (root.Watcher is not null) retired.Add(root.Watcher);
                        if (root.Watcher is not null || root.RevalidateWhenAvailable || root.RevalidateWhenWatched)
                        {
                            foreach (var replacement in requested.Where(path => ScopesOverlap(root.Path, root.IncludeSubdirectories, path, Recurse(path))))
                            {
                                var overlap = Under(replacement, root.Path) ? replacement : root.Path;
                                var overlapRecursive = root.Path.Equals(replacement, StringComparison.OrdinalIgnoreCase)
                                    ? root.IncludeSubdirectories && Recurse(replacement)
                                    : overlap == replacement ? Recurse(replacement) : root.IncludeSubdirectories;
                                if (!requested.Any(cover => _roots.TryGetValue(cover, out var live) && live.Watcher is not null &&
                                    live.IncludeSubdirectories == Recurse(cover) &&
                                    (live.IncludeSubdirectories ? Under(overlap, cover) : !overlapRecursive && overlap.Equals(cover, StringComparison.OrdinalIgnoreCase))))
                                    uncoveredReplacements.Add(replacement);
                            }
                        }
                        // A temporary browse watcher can overlap a retained library scope.
                        // Transfer pending reconciliation intent before retiring its old owner.
                        if (_dirty.Contains(root.Path) || _revalidateContent.Contains(root.Path))
                            foreach (var replacement in requested.Where(path => ScopesOverlap(root.Path, root.IncludeSubdirectories, path, Recurse(path))))
                            {
                                _dirty.Add(replacement);
                                if (_revalidateContent.Contains(root.Path)) _revalidateContent.Add(replacement);
                            }
                        _roots.Remove(root.Path); _dirty.Remove(root.Path); _revalidateContent.Remove(root.Path);
                    }
                    else if (root.IncludeSubdirectories != Recurse(root.Path))
                    {
                        root.IncludeSubdirectories = Recurse(root.Path);
                        _dirty.Add(root.Path);
                        if (root.Watcher is not null)
                        {
                            retired.Add(root.Watcher); root.Watcher = null; root.NextProbe = default;
                            // Replacing a subscription creates a real notification gap, unlike a quiet pause.
                            root.RevalidateWhenAvailable = true;
                            root.RevalidateWhenWatched = true;
                        }
                    }
                }
                foreach (var path in requested)
                {
                    if (!_roots.ContainsKey(path)) { _roots.Add(path, new Root(path, Recurse(path))); _dirty.Add(path); }
                    if (uncoveredReplacements.Contains(path))
                    {
                        // Retiring the only covering subscription creates a real notification gap.
                        _roots[path].RevalidateWhenAvailable = true;
                        _roots[path].RevalidateWhenWatched = true;
                        _roots[path].NextProbe = default;
                    }
                    if (filtersChanged) _dirty.Add(path);
                    _roots[path].PendingState = _roots[path].LastState;
                }
                foreach (var path in _paths.Keys.ToArray())
                {
                    string? owner = _paths[path];
                    if (!_roots.ContainsKey(owner) || !IsObservedFile(path, owner))
                        owner = _roots.Keys.FirstOrDefault(root => IsObservedFile(path, root));
                    if (owner is null || !IsObservedFile(path, owner)) _paths.Remove(path);
                    else _paths[path] = owner;
                }
                foreach (var path in _directories.Keys.ToArray())
                {
                    string? owner = _directories[path];
                    if (!_roots.ContainsKey(owner) || !IsObserved(path, owner))
                        owner = _roots.Keys.FirstOrDefault(root => IsObserved(path, root));
                    if (owner is null) _directories.Remove(path);
                    else _directories[path] = owner;
                }
                _callbackRetryAt = default;
            }
            await Task.Run(() => { foreach (var watcher in retired) watcher.Dispose(); }).ConfigureAwait(false);
            lock (_sync) if (!_explicitlyPaused) StartGeneration();
            Wake();
        }
        finally { _control.Release(); }
    }

    /// <summary>Drains actual readers/callbacks; native directory watchers continue queuing bounded notifications.</summary>
    public async Task PauseAsync()
    {
        await _control.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            lock (_sync) _explicitlyPaused = true;
            // Windows FileSystemWatcher holds directory notification handles, not media readers.
            // Keep them alive so a modal review does not create an unobserved content gap.
            await PauseCoreAsync(closeWatchers: false).ConfigureAwait(false);
        }
        finally { _control.Release(); }
    }

    public async Task ResumeAsync()
    {
        await _control.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                _explicitlyPaused = false;
                if (!_paused) return;
                _generation++;
                // Notifications collected during the pause still require targeted content checks.
                _callbackRetryAt = default;
                foreach (var root in _roots.Values) { _dirty.Add(root.Path); root.NextProbe = default; root.NextWatchAttempt = default; }
                StartGeneration();
            }
            Wake();
        }
        finally { _control.Release(); }
    }

    public void RequestReconciliation()
    {
        lock (_sync)
        {
            if (_disposed) return;
            foreach (var root in _roots.Keys) _dirty.Add(root);
        }
        Wake();
    }

    private void StartGeneration()
    {
        _generationCancellation.Dispose();
        _generationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _paused = false;
    }

    private async Task PauseCoreAsync(bool closeWatchers = true)
    {
        CancellationTokenSource cancellation;
        lock (_sync) { _paused = true; cancellation = _generationCancellation; }
        await cancellation.CancelAsync().ConfigureAwait(false);
        Wake();
        await _work.WaitAsync().ConfigureAwait(false);
        try
        {
            // FileSystemWatcher.Dispose may wait for native work. Keep it off the caller's UI thread.
            await Task.Run(() =>
            {
                if (closeWatchers)
                    foreach (var root in _roots.Values)
                    {
                        root.RevalidateWhenAvailable = true;
                        root.RevalidateWhenWatched = root.Watcher is not null || root.RevalidateWhenWatched;
                        root.Watcher?.Dispose(); root.Watcher = null;
                    }
            }).ConfigureAwait(false);
        }
        finally { _work.Release(); }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await _wake.WaitAsync(_options.PollInterval, _lifetime.Token).ConfigureAwait(false);
                await _work.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    long generation; CancellationToken token;
                    lock (_sync)
                    {
                        if (_paused) continue;
                        generation = _generation; token = _generationCancellation.Token;
                    }
                    MaintainRoots(generation, token);
                    token.ThrowIfCancellationRequested();
                    var batch = TakeBatch(generation);
                    if (batch is null) continue;
                    lock (_sync) _activeBatch = batch;
                    try
                    {
                        await _callback(batch, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        lock (_sync)
                        {
                            var completedAt = UtcNow;
                            foreach (var path in batch.ReconcileRoots)
                            {
                                if (!_roots.TryGetValue(path, out var reconciled)) continue;
                                // A long scan must be followed by a quiet interval, not another
                                // immediately overdue periodic scan. Real events arriving during
                                // this callback remain in _dirty and are not acknowledged here.
                                reconciled.NextReconciliation = completedAt + _options.ReconciliationInterval;
                                _completedReconciliations++;
                                _lastReconciliationCompletedAtUtc = completedAt;
                            }
                            _completedDirectoryReconciliations += batch.DirectoryChanges?.Count ?? 0;
                            foreach (var state in batch.RootStates.Where(state => state.ErrorCode == "ConsumerFailedRetryScheduled"))
                                if (_roots.TryGetValue(state.Path, out var root)) root.PendingState = root.LastState;
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        lock (_sync)
                        {
                            foreach (var path in batch.ReconcileRoots) _dirty.Add(path);
                            foreach (var path in batch.RevalidateContentRoots ?? []) _revalidateContent.Add(path);
                            // Replaying an interrupted path check does not justify invalidating
                            // every cached hash in its root. AddPath escalates only on real overflow.
                            foreach (var path in batch.ChangedPaths)
                                foreach (var root in _roots.Keys.Where(root => Under(path, root))) AddPath(path, root);
                            foreach (var change in batch.DirectoryChanges ?? [])
                                if (_roots.ContainsKey(change.OwnerRoot)) AddDirectory(change.Path, change.OwnerRoot);
                        }
                    }
                    catch
                    {
                        // A failed consumer cannot acknowledge dirtiness. Retry a complete root reconciliation.
                        lock (_sync)
                        {
                            var affected = batch.ReconcileRoots.Concat(batch.RootStates.Select(state => state.Path))
                                .Concat((batch.DirectoryChanges ?? []).Select(change => change.OwnerRoot))
                                .Concat(_roots.Keys.Where(root => batch.ChangedPaths.Any(path => Under(path, root))))
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            foreach (var path in affected)
                            {
                                _dirty.Add(path);
                                if ((batch.RevalidateContentRoots ?? []).Contains(path) || batch.ChangedPaths.Any(changed => Under(changed, path)))
                                    _revalidateContent.Add(path);
                                var root = _roots[path];
                                root.PendingState = new(path, FileAvailability.NeedsVerification, root.Watcher is not null, "ConsumerFailedRetryScheduled");
                            }
                            _callbackRetryAt = UtcNow + _options.MaximumRetryDelay;
                        }
                    }
                    finally { lock (_sync) _activeBatch = null; }
                }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { }
                finally { _work.Release(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void MaintainRoots(long generation, CancellationToken token)
    {
        foreach (var root in _roots.Values)
        {
            token.ThrowIfCancellationRequested();
            var now = UtcNow;
            bool restart;
            lock (_sync) { restart = root.RestartRequested; root.RestartRequested = false; }
            if (restart) { root.RevalidateWhenWatched = true; root.Watcher?.Dispose(); root.Watcher = null; root.NextProbe = default; }
            if (now < root.NextProbe) continue;
            root.NextProbe = now + _options.RootProbeInterval;
            FileSystemProbeResult result;
            try { result = _probe.ProbeRoot(root.Path); }
            catch { result = new(root.Path, FileAvailability.NeedsVerification, now, ErrorCode: "RootProbeFailed"); }
            token.ThrowIfCancellationRequested();
            var availability = result.Availability;
            string? error = result.ErrorCode;
            if (availability != FileAvailability.Available)
            {
                root.RevalidateWhenAvailable = true;
                root.RevalidateWhenWatched = root.Watcher is not null || root.RevalidateWhenWatched;
                root.Watcher?.Dispose(); root.Watcher = null;
            }
            else
            {
                if (root.RevalidateWhenAvailable)
                {
                    lock (_sync) { _dirty.Add(root.Path); _revalidateContent.Add(root.Path); }
                    root.RevalidateWhenAvailable = false;
                }
            }
            if (availability == FileAvailability.Available && root.Watcher is null)
            {
                if (_roots.Values.Count(candidate => candidate.Watcher is not null) >= _options.MaximumWatchers)
                { availability = FileAvailability.NeedsVerification; error = "WatcherLimitPeriodicFallback"; }
                else if (now >= root.NextWatchAttempt)
                {
                    try
                    {
                        root.Watcher = _factory.Watch(root.Path, root.IncludeSubdirectories,
                            change => OnChange(root, change), _ => OnWatcherError(root));
                        root.Failures = 0; root.NextWatchAttempt = default;
                        lock (_sync)
                        {
                            _dirty.Add(root.Path);
                            if (root.RevalidateWhenWatched) { _revalidateContent.Add(root.Path); root.RevalidateWhenWatched = false; }
                        }
                    }
                    catch
                    {
                        availability = FileAvailability.NeedsVerification; error = "WatcherFailedPeriodicFallback";
                        root.RevalidateWhenWatched = true;
                        root.Failures = Math.Min(root.Failures + 1, 16);
                        root.NextWatchAttempt = now + TimeSpan.FromMilliseconds(Math.Min(_options.MaximumRetryDelay.TotalMilliseconds,
                            _options.RetryDelay.TotalMilliseconds * Math.Pow(2, root.Failures - 1)));
                    }
                }
                else { availability = FileAvailability.NeedsVerification; error = "WatcherRetryPending"; }
            }
            var state = new LibraryRootState(root.Path, availability, root.Watcher is not null, error);
            lock (_sync)
            {
                if (state != root.LastState)
                {
                    root.PendingState = state; root.LastState = state; _dirty.Add(root.Path);
                }
                if (now >= root.NextReconciliation)
                {
                    _dirty.Add(root.Path); root.NextReconciliation = now + _options.ReconciliationInterval;
                }
            }
        }
    }

    private LibraryMonitorBatch? TakeBatch(long generation)
    {
        lock (_sync)
        {
            if (_paused || _generation != generation || UtcNow < _callbackRetryAt) return null;
            var states = _roots.Values.Where(root => root.PendingState is not null).Select(root => root.PendingState!).ToArray();
            foreach (var root in _roots.Values) root.PendingState = null;
            var reconcile = _dirty.Take(_options.MaximumReconcileRootsPerBatch).ToArray();
            foreach (var path in reconcile) _dirty.Remove(path);
            // The future full pass covers directory events already queued for that owner.
            // Events arriving after this point enqueue fresh intent, including during the pass.
            foreach (var path in _directories.Where(pair => reconcile.Contains(pair.Value, StringComparer.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray())
                _directories.Remove(path);
            var directories = _directories.Take(_options.MaximumDirectoriesPerBatch)
                .Select(pair => new LibraryDirectoryChange(pair.Value, pair.Key)).ToArray();
            foreach (var change in directories) _directories.Remove(change.Path);
            var revalidate = reconcile.Where(path => _revalidateContent.Remove(path)).ToArray();
            // A bounded first-event debounce cannot be postponed forever by a continuous copy.
            var paths = UtcNow - _firstChange >= _options.Debounce
                ? _paths.Keys.Take(_options.MaximumPathsPerBatch).ToArray() : [];
            foreach (var path in paths) _paths.Remove(path);
            if (states.Length + reconcile.Length + paths.Length + directories.Length == 0) return null;
            return new(generation, paths, reconcile, states, revalidate, directories);
        }
    }

    private void OnChange(Root watchedRoot, LibraryWatchEvent change)
    {
        var queued = false;
        lock (_sync)
        {
            var root = watchedRoot.Path;
            if (_disposed || !_roots.TryGetValue(root, out var current) || !ReferenceEquals(current, watchedRoot)) return;
            if (change.RequiresReconciliation)
            {
                queued = AddDirectory(change.Path, root);
                if (change.OldPath is not null) queued |= AddDirectory(change.OldPath, root);
            }
            else
            {
                queued = AddPath(change.Path, root);
                if (change.OldPath is not null) queued |= AddPath(change.OldPath, root);
            }
        }
        if (queued) Wake();
    }

    private bool AddPath(string path, string root)
    {
        try { path = Normalize(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { _dirty.Add(root); return true; }
        if (!IsObservedFile(path, root)) return false;
        if (_paths.ContainsKey(path)) return false;
        if (_paths.Count >= _options.MaximumPendingPaths)
        {
            // Recover every overflowed root, rather than silently dropping a path notification.
            _dirty.Add(root); _revalidateContent.Add(root); return true;
        }
        if (_paths.Count == 0) _firstChange = UtcNow;
        _paths.Add(path, root);
        return true;
    }

    private bool AddDirectory(string path, string root)
    {
        try { path = Normalize(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { _dirty.Add(root); return true; }
        if (!IsObserved(path, root)) return false;
        if (_dirty.Contains(root)) return false; // A not-yet-started full pass already covers it.
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) { _dirty.Add(root); return true; }
        // Directory notifications identify a changed subtree, not a content change to every
        // photo on the drive. Keep old and new rename paths, including nonexistent old paths.
        if (_directories.Any(pair => pair.Value.Equals(root, StringComparison.OrdinalIgnoreCase) && Under(path, pair.Key))) return false;
        foreach (var child in _directories.Where(pair => pair.Value.Equals(root, StringComparison.OrdinalIgnoreCase) && Under(pair.Key, path)).Select(pair => pair.Key).ToArray())
            _directories.Remove(child);
        if (_directories.Count >= _options.MaximumPendingDirectories)
        {
            // Overflow must retain discovery intent, but directory names alone do not prove
            // changed media bytes and must not invalidate the entire root's cached metadata.
            _dirty.Add(root);
            foreach (var child in _directories.Where(pair => pair.Value.Equals(root, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray())
                _directories.Remove(child);
            return true;
        }
        _directories[path] = root;
        return true;
    }

    private bool IsObserved(string path, string root)
    {
        if (!Under(path, root) || IsIgnored(path)) return false;
        if (_roots.TryGetValue(root, out var scope) && !scope.IncludeSubdirectories &&
            !path.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase)) return false;
        try { return _configuration.ShouldObservePath?.Invoke(path) != false; }
        catch { _dirty.Add(root); return false; }
    }

    private bool IsObservedFile(string path, string root)
    {
        try { if (_configuration.ShouldObserveFilePath?.Invoke(path) == false) return false; }
        catch { _dirty.Add(root); return false; }
        return IsObserved(path, root);
    }

    private bool IsIgnored(string path)
    {
        if (path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(component => component.StartsWith(".photoshelf-", StringComparison.OrdinalIgnoreCase))) return true;
        return (_configuration.IgnoredDirectories ?? []).Any(ignored => Under(path, ignored));
    }

    private void OnWatcherError(Root root)
    {
        lock (_sync)
        {
            if (_disposed || !_roots.TryGetValue(root.Path, out var current) || !ReferenceEquals(current, root)) return;
            root.RestartRequested = true;
            root.RevalidateWhenWatched = true;
            root.Failures = Math.Min(root.Failures + 1, 16);
            root.NextWatchAttempt = UtcNow + TimeSpan.FromMilliseconds(Math.Min(_options.MaximumRetryDelay.TotalMilliseconds,
                _options.RetryDelay.TotalMilliseconds * Math.Pow(2, root.Failures - 1)));
            _dirty.Add(root.Path); _revalidateContent.Add(root.Path);
            root.PendingState = new(root.Path, FileAvailability.NeedsVerification, false, "WatcherEventsLostReconciliationRequired");
            // Record the observed error so an immediate successful restart publishes Available
            // even if it matches the state from before this error (e.g. after a long pause).
            root.LastState = root.PendingState;
        }
        Wake();
    }

    private static bool Under(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool ScopesOverlap(string first, bool firstRecursive, string second, bool secondRecursive) =>
        first.Equals(second, StringComparison.OrdinalIgnoreCase) || firstRecursive && Under(second, first) || secondRecursive && Under(first, second);
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        await _control.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await PauseCoreAsync().ConfigureAwait(false);
            await _lifetime.CancelAsync().ConfigureAwait(false);
            Wake();
            await _worker.ConfigureAwait(false);
            _generationCancellation.Dispose(); _lifetime.Dispose();
        }
        finally { _control.Release(); }
    }

    // FIFO membership prevents a root receiving a continuous event stream from starving
    // other roots. Both collections are bounded by the configured root limit.
    private sealed class DirtyRootQueue
    {
        private readonly LinkedList<string> _order = new();
        private readonly Dictionary<string, LinkedListNode<string>> _members = new(StringComparer.OrdinalIgnoreCase);
        public int Count => _members.Count;
        public void Add(string path)
        {
            if (!_members.ContainsKey(path)) _members.Add(path, _order.AddLast(path));
        }
        public void Remove(string path)
        {
            if (_members.Remove(path, out var node)) _order.Remove(node);
        }
        public bool Contains(string path) => _members.ContainsKey(path);
        public IEnumerable<string> Take(int count) => _order.Take(count);
    }

    private sealed class Root(string path, bool includeSubdirectories)
    {
        public string Path { get; } = path;
        public bool IncludeSubdirectories = includeSubdirectories;
        public IDisposable? Watcher;
        public DateTime NextProbe, NextReconciliation, NextWatchAttempt;
        public int Failures;
        public bool RestartRequested, RevalidateWhenAvailable, RevalidateWhenWatched;
        public LibraryRootState? LastState, PendingState;
    }
}

public sealed class FileSystemLibraryWatcherFactory : ILibraryWatcherFactory
{
    public IDisposable Watch(string root, bool includeSubdirectories, Action<LibraryWatchEvent> onChange, Action<Exception> onError)
    {
        var files = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
            InternalBufferSize = 16 * 1024
        };
        // A separate bounded DirectoryName stream tells us about deleted/moved directories
        // without calling File.GetAttributes from a native event callback or rescanning the
        // complete root after every individual image creation/deletion.
        var directories = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.DirectoryName,
            InternalBufferSize = 8 * 1024
        };
        files.Created += (_, args) => onChange(new(args.FullPath));
        files.Deleted += (_, args) => onChange(new(args.FullPath));
        files.Changed += (_, args) => onChange(new(args.FullPath));
        files.Renamed += (_, args) => onChange(new(args.FullPath, args.OldFullPath));
        directories.Created += (_, args) => onChange(new(args.FullPath, RequiresReconciliation: true));
        directories.Deleted += (_, args) => onChange(new(args.FullPath, RequiresReconciliation: true));
        directories.Renamed += (_, args) => onChange(new(args.FullPath, args.OldFullPath, RequiresReconciliation: true));
        files.Error += (_, args) => onError(args.GetException());
        directories.Error += (_, args) => onError(args.GetException());
        try { files.EnableRaisingEvents = true; directories.EnableRaisingEvents = true; return new WatchPair(files, directories); }
        catch { files.Dispose(); directories.Dispose(); throw; }
    }

    private sealed class WatchPair(FileSystemWatcher files, FileSystemWatcher directories) : IDisposable
    {
        public void Dispose() { files.Dispose(); directories.Dispose(); }
    }
}
