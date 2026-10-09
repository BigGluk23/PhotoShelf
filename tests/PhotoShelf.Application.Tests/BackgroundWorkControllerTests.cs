using PhotoShelf.Application.Background;
using PhotoShelf.Application.Diagnostics;
using PhotoShelf.Application.Metadata;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class BackgroundWorkControllerTests
{
    [Fact]
    public void ModesInteractionAndBatteryBoundPacingWithoutStarvingWork()
    {
        var time = new Clock(); var control = new BackgroundWorkController(time);
        var unit = TimeSpan.FromMilliseconds(100);
        Assert.Equal(100, control.DelayFor(unit).TotalMilliseconds);
        control.Mode = BackgroundLoadMode.Fast;
        Assert.Equal(1, control.DelayFor(unit).TotalMilliseconds);
        control.NoteInteraction();
        Assert.Equal(400, control.DelayFor(unit).TotalMilliseconds);
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(1, control.DelayFor(unit).TotalMilliseconds);
        control.SetPowerState(true);
        Assert.Equal(400, control.DelayFor(unit).TotalMilliseconds);
        control.SetPowerState(false); control.Mode = BackgroundLoadMode.Quiet;
        Assert.Equal(300, control.DelayFor(unit).TotalMilliseconds);
        Assert.Equal(500, control.DelayFor(TimeSpan.FromHours(2)).TotalMilliseconds);
        control.Mode = (BackgroundLoadMode)99;
        Assert.Equal(BackgroundLoadMode.Balanced, control.Mode);
    }

    [Fact]
    public void SlowReadIsDistinguishedFromPacingWaitingAndFinishedWork()
    {
        var time = new Clock(); var control = new BackgroundWorkController(time);
        using var activity = control.Begin(BackgroundTaskKind.Metadata);
        activity.SetPhase(BackgroundTaskPhase.Reading);
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(Assert.Single(control.Snapshot().Tasks).LongRunningRead);
        activity.Progress(); activity.SetPhase(BackgroundTaskPhase.Pacing);
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(Assert.Single(control.Snapshot().Tasks).LongRunningRead);
        activity.SetPhase(BackgroundTaskPhase.WaitingForReader);
        Assert.False(Assert.Single(control.Snapshot().Tasks).LongRunningRead);
        activity.Finish(BackgroundTaskPhase.Completed);
        Assert.Equal(1, Assert.Single(control.Snapshot().Tasks).Completed);
    }

    [Fact]
    public async Task SuspendWaitsAtBoundaryAndCancellationDoesNotFakeReadCompletion()
    {
        var control = new BackgroundWorkController();
        using var activity = control.Begin(BackgroundTaskKind.Fingerprints);
        activity.SetPhase(BackgroundTaskPhase.Reading);
        control.SetSuspended(true);
        using var cancellation = new CancellationTokenSource();
        var wait = control.CreatePacer().CheckpointAsync(cancellation.Token, activity);
        Assert.False(wait.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(BackgroundTaskPhase.Reading, Assert.Single(control.Snapshot().Tasks).Phase);
        var resume = control.CreatePacer().CheckpointAsync(CancellationToken.None);
        Assert.False(resume.IsCompleted);
        control.SetSuspended(false);
        await resume.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ResumeDoesNotCountSleepAsStuckReadAndOldLeaseCannotReplaceSuccessor()
    {
        var time = new Clock(); var control = new BackgroundWorkController(time);
        using var old = control.Begin(BackgroundTaskKind.Scan);
        old.SetPhase(BackgroundTaskPhase.Reading);
        control.SetSuspended(true); time.Advance(TimeSpan.FromHours(12));
        Assert.False(Assert.Single(control.Snapshot().Tasks).LongRunningRead);
        control.SetSuspended(false);
        Assert.Equal(0, Assert.Single(control.Snapshot().Tasks).SinceProgressSeconds);
        using var next = control.Begin(BackgroundTaskKind.Scan);
        old.Progress(100); old.Finish(BackgroundTaskPhase.Completed);
        Assert.Equal(0, Assert.Single(control.Snapshot().Tasks).Completed);
    }

    [Fact]
    public void HistoryIsBoundedAndExportOnlyContainsStructuredAnonymousFields()
    {
        var control = new BackgroundWorkController();
        using var activity = control.Begin(BackgroundTaskKind.Scan);
        for (var i = 0; i < 1000; i++) control.RecordSample(i, i, double.NaN, i, 1);
        Assert.Equal(BackgroundWorkController.HistoryCapacity, control.History().Length);
        var json = SupportSnapshot.Create("1.11.3-ultra", false, 0, control);
        Assert.Contains("\"history\"", json);
        Assert.DoesNotContain("NaN", json);
        Assert.DoesNotContain(Environment.UserName + "/", json);
        Assert.All(control.History(), s => Assert.InRange(s.CpuPercent, 0, 100));
    }

    [Fact]
    public void TransientMetadataBackoffSurvivesReloadAndNeverErasesKnownDate()
    {
        var date = new DateTime(2020, 1, 2);
        DateTime? attempted = null, retry = null;
        foreach (var minutes in new[] { 5, 30, 360, 1440, 1440 })
        {
            var result = new CaptureDateReadResult(MetadataReadStatus.TransientError)
                .WithRetryBackoff(attempted, retry);
            attempted = retry ?? DateTime.UtcNow;
            retry = result.RetryAtUtc(attempted.Value);
            Assert.Equal(TimeSpan.FromMinutes(minutes), retry - attempted);
            Assert.Equal(date, result.ApplyTo(date));
        }
        var terminal = new CaptureDateReadResult(MetadataReadStatus.Corrupt).WithRetryBackoff(attempted, retry);
        Assert.Null(terminal.RetryAtUtc(DateTime.UtcNow));
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
}
