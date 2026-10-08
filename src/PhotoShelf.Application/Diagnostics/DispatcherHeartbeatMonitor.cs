namespace PhotoShelf.Application.Diagnostics;

/// <summary>Retains completed stalls between samples and observes a heartbeat that is still overdue.</summary>
public sealed class DispatcherHeartbeatMonitor(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private long _lastTick = (timeProvider ?? TimeProvider.System).GetTimestamp();
    private double _peak;
    public const double IntervalMilliseconds = 100;

    public double Tick()
    {
        lock (_sync)
        {
            var now = _time.GetTimestamp();
            var delay = Overdue(now);
            _peak = Math.Max(_peak, delay);
            _lastTick = now;
            return delay;
        }
    }

    public double TakeDelayMilliseconds()
    {
        lock (_sync)
        {
            var delay = Math.Max(_peak, Overdue(_time.GetTimestamp()));
            _peak = 0;
            return delay;
        }
    }

    private double Overdue(long now) => Math.Max(0,
        _time.GetElapsedTime(_lastTick, now).TotalMilliseconds - IntervalMilliseconds);
}
