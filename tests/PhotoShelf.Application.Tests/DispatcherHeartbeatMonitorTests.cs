using PhotoShelf.Application.Diagnostics;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class DispatcherHeartbeatMonitorTests
{
    [Fact]
    public void RecoveredStallSurvivesHealthyTicksUntilSampled()
    {
        var clock = new Clock(); var monitor = new DispatcherHeartbeatMonitor(clock);
        clock.Advance(2100);
        Assert.Equal(2000, monitor.Tick());
        for (var i = 0; i < 20; i++) { clock.Advance(100); Assert.Equal(0, monitor.Tick()); }
        Assert.Equal(2000, monitor.TakeDelayMilliseconds());
        Assert.Equal(0, monitor.TakeDelayMilliseconds());
    }

    [Fact]
    public void BackgroundSamplesSeeStallBeforeDispatcherRecovers()
    {
        var clock = new Clock(); var monitor = new DispatcherHeartbeatMonitor(clock);
        clock.Advance(5100);
        Assert.Equal(5000, monitor.TakeDelayMilliseconds());
        clock.Advance(5000);
        Assert.Equal(10000, monitor.TakeDelayMilliseconds());
        Assert.Equal(10000, monitor.Tick());
        Assert.Equal(10000, monitor.TakeDelayMilliseconds());
        clock.Advance(100); monitor.Tick();
        Assert.Equal(0, monitor.TakeDelayMilliseconds());
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(int milliseconds) => _ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }
}
