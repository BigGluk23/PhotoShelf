namespace PhotoShelf.Application.Background;

public enum BackgroundLoadMode { Quiet, Balanced, Fast }
public enum BackgroundTaskKind { Scan, Metadata, Fingerprints }
public enum BackgroundTaskPhase { WaitingForReader, Preparing, Queued, Reading, Saving, Pacing, Completed, Cancelled, Failed }
public sealed record BackgroundTaskSnapshot(BackgroundTaskKind Kind, BackgroundTaskPhase Phase, long Completed,
    long Errors, double ElapsedSeconds, double SinceProgressSeconds, double PhaseSeconds, double ItemsPerSecond,
    bool LongRunningRead);
public sealed record BackgroundLoadSnapshot(BackgroundLoadMode Mode, bool OnBattery, bool UserActive,
    bool Suspended, int Pending, int Running, IReadOnlyList<BackgroundTaskSnapshot> Tasks);
public sealed record BackgroundSample(DateTimeOffset At, double CpuPercent, long WorkingSetBytes,
    double DispatcherDelayMs, BackgroundLoadSnapshot Work);

/// <summary>Cooperative pacing and bounded, path-free diagnostics. Never cancels or abandons a reader.</summary>
public sealed class BackgroundWorkController(TimeProvider? timeProvider = null)
{
    public static BackgroundWorkController Shared { get; } = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Dictionary<BackgroundTaskKind, Activity> _activities = [];
    private readonly Queue<BackgroundSample> _history = new();
    private BackgroundLoadMode _mode = BackgroundLoadMode.Balanced;
    private bool _onBattery, _suspended;
    private long? _lastInput;
    private TaskCompletionSource? _resume;
    public const int HistoryCapacity = 120; // Ten minutes at the Desktop's five-second cadence.

    public BackgroundLoadMode Mode
    {
        get { lock (_sync) return _mode; }
        set { lock (_sync) _mode = Enum.IsDefined(value) ? value : BackgroundLoadMode.Balanced; }
    }
    public void SetPowerState(bool onBattery) { lock (_sync) _onBattery = onBattery; }
    public void SetSuspended(bool suspended)
    {
        lock (_sync)
        {
            if (_suspended == suspended) return;
            _suspended = suspended;
            if (suspended) _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
            else
            {
                // Sleep duration is not evidence of a stuck file reader.
                foreach (var activity in _activities.Values) activity.ResumeClock();
                _resume?.TrySetResult(); _resume = null;
            }
        }
    }
    private Task WaitUntilResumedAsync(CancellationToken token)
    { lock (_sync) return _resume?.Task.WaitAsync(token) ?? Task.CompletedTask; }
    public void NoteInteraction() { lock (_sync) _lastInput = _time.GetTimestamp(); }
    private bool IsInteractive(long now) => _lastInput is { } input && _time.GetElapsedTime(input, now) < TimeSpan.FromSeconds(3);

    public TimeSpan DelayFor(TimeSpan work)
    {
        lock (_sync)
        {
            // Even Fast yields. Battery/interaction override throughput preference.
            var factor = _mode switch { BackgroundLoadMode.Quiet => 3.0, BackgroundLoadMode.Fast => 0.0, _ => 1.0 };
            if (_onBattery || IsInteractive(_time.GetTimestamp()) || _suspended) factor = Math.Max(factor, 4.0);
            return TimeSpan.FromMilliseconds(Math.Clamp(work.TotalMilliseconds * factor, 1, 500));
        }
    }

    public Pacer CreatePacer() => new(this);
    public Activity Begin(BackgroundTaskKind kind)
    {
        lock (_sync)
        {
            var activity = new Activity(this, kind, _time.GetTimestamp());
            _activities[kind] = activity; // Old leases cannot overwrite a successor's diagnostics.
            return activity;
        }
    }

    public BackgroundLoadSnapshot Snapshot(int pending = 0, int running = 0)
    {
        lock (_sync)
        {
            var now = _time.GetTimestamp();
            return new(_mode, _onBattery, IsInteractive(now), _suspended, Math.Max(0, pending), Math.Max(0, running),
                _activities.Values.OrderBy(a => a.Kind).Select(a => a.Snapshot(now)).ToArray());
        }
    }
    public void RecordSample(double cpuPercent, long workingSetBytes, double dispatcherDelayMs, int pending, int running)
    {
        lock (_sync)
        {
            if (_history.Count == HistoryCapacity) _history.Dequeue();
            _history.Enqueue(new(_time.GetUtcNow(), Finite(cpuPercent, 100), Math.Max(0, workingSetBytes),
                Finite(dispatcherDelayMs, 86400000), Snapshot(pending, running)));
        }
    }
    public BackgroundSample[] History() { lock (_sync) return _history.ToArray(); }
    private static double Finite(double value, double max) => double.IsFinite(value) ? Math.Clamp(value, 0, max) : 0;

    public sealed class Pacer
    {
        private readonly BackgroundWorkController _owner;
        private long _started;
        internal Pacer(BackgroundWorkController owner) { _owner = owner; _started = owner._time.GetTimestamp(); }
        public async Task CheckpointAsync(CancellationToken token, Activity? activity = null)
        {
            token.ThrowIfCancellationRequested();
            await _owner.WaitUntilResumedAsync(token).ConfigureAwait(false);
            var elapsed = _owner._time.GetElapsedTime(_started);
            if (elapsed < TimeSpan.FromMilliseconds(75)) return;
            var previous = activity?.Phase;
            activity?.SetPhase(BackgroundTaskPhase.Pacing);
            try { await Task.Delay(_owner.DelayFor(elapsed), _owner._time, token).ConfigureAwait(false); }
            finally
            {
                _started = _owner._time.GetTimestamp();
                if (previous is { } phase) activity?.SetPhase(phase);
            }
        }
    }

    public sealed class Activity : IDisposable
    {
        private readonly BackgroundWorkController _owner;
        internal BackgroundTaskKind Kind { get; }
        private readonly long _started;
        private long _lastProgress, _phaseStarted, _completed, _errors;
        private long? _finished;
        public BackgroundTaskPhase Phase { get; private set; } = BackgroundTaskPhase.Preparing;
        internal Activity(BackgroundWorkController owner, BackgroundTaskKind kind, long started)
        { _owner = owner; Kind = kind; _started = _lastProgress = _phaseStarted = started; }
        public void SetPhase(BackgroundTaskPhase phase)
        {
            lock (_owner._sync)
            {
                if (_finished is not null) return;
                Phase = phase; _phaseStarted = _owner._time.GetTimestamp();
            }
        }
        public void Progress(long completed = 1, long errors = 0)
        {
            lock (_owner._sync)
            {
                if (_finished is not null) return;
                _completed += Math.Max(0, completed); _errors += Math.Max(0, errors);
                _lastProgress = _owner._time.GetTimestamp();
            }
        }
        public void Finish(BackgroundTaskPhase phase)
        {
            if (phase is not (BackgroundTaskPhase.Completed or BackgroundTaskPhase.Cancelled or BackgroundTaskPhase.Failed))
                throw new ArgumentOutOfRangeException(nameof(phase));
            lock (_owner._sync) { if (_finished is null) { SetPhase(phase); _finished = _owner._time.GetTimestamp(); } }
        }
        internal void ResumeClock()
        {
            if (_finished is null) _lastProgress = _phaseStarted = _owner._time.GetTimestamp();
        }
        internal BackgroundTaskSnapshot Snapshot(long now)
        {
            var end = _finished ?? now;
            var elapsed = _owner._time.GetElapsedTime(_started, end).TotalSeconds;
            var age = _owner._time.GetElapsedTime(_lastProgress, end).TotalSeconds;
            var phaseAge = _owner._time.GetElapsedTime(_phaseStarted, end).TotalSeconds;
            return new(Kind, Phase, _completed, _errors, elapsed, age, phaseAge, _completed / Math.Max(1, elapsed),
                _finished is null && Phase == BackgroundTaskPhase.Reading && phaseAge >= 60 && !_owner._suspended);
        }
        // An unwound exception must never be displayed as successful completion.
        public void Dispose() { Finish(BackgroundTaskPhase.Failed); }
    }
}
