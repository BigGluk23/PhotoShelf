namespace PhotoShelf.Application.Catalog;

public sealed record LibraryMonitorConfiguration(IReadOnlyList<string> Roots,
    IReadOnlyList<string>? IgnoredDirectories = null, bool IncludeSubdirectories = true,
    Func<string, bool>? ShouldObservePath = null);

public sealed record LibraryRootState(string Path, FileAvailability Availability, bool IsWatching, string? ErrorCode = null);
public sealed record LibraryMonitorBatch(long Generation, IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<string> ReconcileRoots, IReadOnlyList<LibraryRootState> RootStates,
    IReadOnlyList<string>? RevalidateContentRoots = null);

public sealed record LibraryMonitorOptions
{
    public int MaximumRoots { get; init; } = 4096;
    public int MaximumWatchers { get; init; } = 32;
    public int MaximumPendingPaths { get; init; } = 1024;
    public int MaximumPathsPerBatch { get; init; } = 256;
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
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
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

    public LibraryChangeCoordinator(Func<LibraryMonitorBatch, CancellationToken, Task> callback,
        LibraryMonitorOptions? options = null, ILibraryWatcherFactory? watcherFactory = null,
        IFileSystemObservationProbe? probe = null)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _options = options ?? new();
        if (_options.MaximumRoots < 1 || _options.MaximumWatchers < 0 || _options.MaximumPendingPaths < 1
            || _options.MaximumPathsPerBatch < 1 || _options.MaximumReconcileRootsPerBatch < 1
            || _options.PollInterval <= TimeSpan.Zero || _options.Debounce < TimeSpan.Zero
            || _options.RootProbeInterval <= TimeSpan.Zero || _options.ReconciliationInterval <= TimeSpan.Zero
            || _options.RetryDelay <= TimeSpan.Zero || _options.MaximumRetryDelay < _options.RetryDelay)
            throw new ArgumentOutOfRangeException(nameof(options));
        _factory = watcherFactory ?? new FileSystemLibraryWatcherFactory();
        _probe = probe ?? new FileSystemObservationProbe();
        _worker = Task.Run(RunAsync);
    }

    public long Generation { get { lock (_sync) return _generation; } }
    public int PendingPathCount { get { lock (_sync) return _paths.Count; } }

    public async Task ConfigureAsync(LibraryMonitorConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // Normalize strings only; configuring from the UI performs no filesystem I/O.
        var roots = configuration.Roots.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).Take(_options.MaximumRoots + 1).ToArray();
        if (roots.Length > _options.MaximumRoots)
            throw new ArgumentException($"Monitoring supports at most {_options.MaximumRoots} roots; no roots were silently discarded.", nameof(configuration));
        var ignored = (configuration.IgnoredDirectories ?? []).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).Take(_options.MaximumRoots + 1).ToArray();
        if (ignored.Length > _options.MaximumRoots) throw new ArgumentException("Too many ignored directories.", nameof(configuration));
        await _control.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await PauseCoreAsync(closeWatchers: false).ConfigureAwait(false);
            List<IDisposable> retired = [];
            lock (_sync)
            {
                _generation++;
                var filtersChanged = _configuration.IncludeSubdirectories != configuration.IncludeSubdirectories
                    || _configuration.ShouldObservePath != configuration.ShouldObservePath
                    || !(_configuration.IgnoredDirectories ?? []).SequenceEqual(ignored, StringComparer.OrdinalIgnoreCase);
                var recursionChanged = _configuration.IncludeSubdirectories != configuration.IncludeSubdirectories;
                _configuration = configuration with { Roots = roots, IgnoredDirectories = ignored };
                var requested = roots.Where(path => !IsIgnored(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var uncoveredReplacements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in _roots.Values.ToArray())
                {
                    if (!requested.Contains(root.Path))
                    {
                        if (root.Watcher is not null) retired.Add(root.Watcher);
                        if (root.Watcher is not null || root.RevalidateWhenAvailable || root.RevalidateWhenWatched)
                        {
                            foreach (var replacement in requested.Where(path => Under(root.Path, path) || Under(path, root.Path)))
                            {
                                var overlap = Under(replacement, root.Path) ? replacement : root.Path;
                                if (!requested.Any(cover => Under(overlap, cover) && _roots.TryGetValue(cover, out var live) && live.Watcher is not null))
                                    uncoveredReplacements.Add(replacement);
                            }
                        }
                        // A temporary browse watcher can overlap a retained library scope.
                        // Transfer pending reconciliation intent before retiring its old owner.
                        if (_dirty.Contains(root.Path) || _revalidateContent.Contains(root.Path))
                            foreach (var replacement in requested.Where(path => Under(root.Path, path) || Under(path, root.Path)))
                            {
                                _dirty.Add(replacement);
                                if (_revalidateContent.Contains(root.Path)) _revalidateContent.Add(replacement);
                            }
                        _roots.Remove(root.Path); _dirty.Remove(root.Path); _revalidateContent.Remove(root.Path);
                    }
                    else if (recursionChanged && root.Watcher is not null)
                    {
                        retired.Add(root.Watcher); root.Watcher = null; root.NextProbe = default;
                        // Replacing a subscription creates a real notification gap, unlike a quiet pause.
                        root.RevalidateWhenAvailable = true;
                        root.RevalidateWhenWatched = true;
                    }
                }
                foreach (var path in requested)
                {
                    if (!_roots.ContainsKey(path)) { _roots.Add(path, new Root(path)); _dirty.Add(path); }
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
                    if (!_roots.ContainsKey(owner))
                        owner = _roots.Keys.FirstOrDefault(root => IsObserved(path, root));
                    if (owner is null || !IsObserved(path, owner)) _paths.Remove(path);
                    else _paths[path] = owner;
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
                    try
                    {
                        await _callback(batch, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        lock (_sync)
                            foreach (var state in batch.RootStates.Where(state => state.ErrorCode == "ConsumerFailedRetryScheduled"))
                                if (_roots.TryGetValue(state.Path, out var root)) root.PendingState = root.LastState;
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
                        }
                    }
                    catch
                    {
                        // A failed consumer cannot acknowledge dirtiness. Retry a complete root reconciliation.
                        lock (_sync)
                        {
                            var affected = batch.ReconcileRoots.Concat(batch.RootStates.Select(state => state.Path))
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
                            _callbackRetryAt = DateTime.UtcNow + _options.MaximumRetryDelay;
                        }
                    }
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
            var now = DateTime.UtcNow;
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
                        root.Watcher = _factory.Watch(root.Path, _configuration.IncludeSubdirectories,
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
            if (_paused || _generation != generation || DateTime.UtcNow < _callbackRetryAt) return null;
            var states = _roots.Values.Where(root => root.PendingState is not null).Select(root => root.PendingState!).ToArray();
            foreach (var root in _roots.Values) root.PendingState = null;
            var reconcile = _dirty.Take(_options.MaximumReconcileRootsPerBatch).ToArray();
            foreach (var path in reconcile) _dirty.Remove(path);
            var revalidate = reconcile.Where(path => _revalidateContent.Remove(path)).ToArray();
            // A bounded first-event debounce cannot be postponed forever by a continuous copy.
            var paths = DateTime.UtcNow - _firstChange >= _options.Debounce
                ? _paths.Keys.Take(_options.MaximumPathsPerBatch).ToArray() : [];
            foreach (var path in paths) _paths.Remove(path);
            if (states.Length + reconcile.Length + paths.Length == 0) return null;
            return new(generation, paths, reconcile, states, revalidate);
        }
    }

    private void OnChange(Root watchedRoot, LibraryWatchEvent change)
    {
        lock (_sync)
        {
            var root = watchedRoot.Path;
            if (_disposed || !_roots.TryGetValue(root, out var current) || !ReferenceEquals(current, watchedRoot)) return;
            if (change.RequiresReconciliation && (IsObserved(change.Path, root)
                || change.OldPath is not null && IsObserved(change.OldPath, root))) _dirty.Add(root);
            AddPath(change.Path, root);
            if (change.OldPath is not null) AddPath(change.OldPath, root);
        }
        Wake();
    }

    private void AddPath(string path, string root)
    {
        try { path = Normalize(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { _dirty.Add(root); return; }
        if (!IsObserved(path, root)) return;
        if (_paths.ContainsKey(path)) return;
        if (_paths.Count >= _options.MaximumPendingPaths)
        {
            // Recover every overflowed root, rather than silently dropping a path notification.
            _dirty.Add(root); _revalidateContent.Add(root); return;
        }
        if (_paths.Count == 0) _firstChange = DateTime.UtcNow;
        _paths.Add(path, root);
    }

    private bool IsObserved(string path, string root)
    {
        if (!Under(path, root) || IsIgnored(path)) return false;
        try { return _configuration.ShouldObservePath?.Invoke(path) != false; }
        catch { _dirty.Add(root); return false; }
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
            root.NextWatchAttempt = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Min(_options.MaximumRetryDelay.TotalMilliseconds,
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

    private sealed class Root(string path)
    {
        public string Path { get; } = path;
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
