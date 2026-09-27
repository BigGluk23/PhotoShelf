using PhotoShelf.Application.Files;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class FileMoveTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "photoshelf-test-" + Guid.NewGuid().ToString("N"));
    private readonly FileMoveService _service = new(new FileMoveOptions { AlwaysCopy = true });
    public FileMoveTests() => Directory.CreateDirectory(_root);
    private string Write(string relative, string content = "original content")
    {
        var path = Path.Combine(_root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path;
    }
    private string Journal => Path.Combine(_root, "journal.jsonl");
    [Fact] public async Task SuccessfulMoveVerifiesContentAndCommitsBeforeRemovingStaging()
    {
        var source = Write("in/p.jpg");
        var plan = _service.Plan(new[] { new MoveRequest(source, null) }, Path.Combine(_root, "out"), CollisionPolicy.Skip);
        var commits = 0;
        var result = await _service.ExecuteAsync(plan, Journal, entry =>
        {
            Assert.Equal("original content", File.ReadAllText(entry.Destination));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(source)!, "*.photoshelf-moving-*"));
            commits++; return Task.CompletedTask;
        }, null, CancellationToken.None);
        Assert.True(Assert.Single(result).Moved, result[0].Error); Assert.Equal(1, commits); Assert.False(File.Exists(source));
        Assert.Contains("completed", File.ReadAllText(Journal));
    }
    [Fact] public async Task NameAppearingAfterPreviewIsNeverOverwritten()
    {
        var source = Write("in/p.jpg"); var destination = Path.Combine(_root, "out");
        var plan = _service.Plan(new[] { new MoveRequest(source, null) }, destination, CollisionPolicy.Rename);
        Write("out/p.jpg", "other");
        var results = await _service.ExecuteAsync(plan, Journal, _ => throw new Exception("must not commit"), null, default);
        Assert.False(results[0].Moved); Assert.Equal("other", File.ReadAllText(plan[0].Destination)); Assert.True(File.Exists(source));
    }
    [Fact] public async Task CatalogFailureRestoresSourceAndKeepsVerifiedDestinationForRecovery()
    {
        var source = Write("in/p.jpg"); var plan = _service.Plan(new[] { new MoveRequest(source, null) }, Path.Combine(_root, "out"), CollisionPolicy.Skip);
        var results = await _service.ExecuteAsync(plan, Journal, _ => throw new IOException("database busy"), null, default);
        Assert.False(results[0].Moved); Assert.Equal("original content", File.ReadAllText(source));
        Assert.Equal("original content", File.ReadAllText(plan[0].Destination)); Assert.Contains("reconciliation_required", File.ReadAllText(Journal));
    }
    [Fact] public async Task SourceChangedAfterPreviewIsNotMoved()
    {
        var source = Write("in/p.jpg"); var plan = _service.Plan(new[] { new MoveRequest(source, null) }, Path.Combine(_root, "out"), CollisionPolicy.Skip);
        File.AppendAllText(source, " changed");
        var results = await _service.ExecuteAsync(plan, Journal, _ => Task.CompletedTask, null, default);
        Assert.False(results[0].Moved); Assert.True(File.Exists(source)); Assert.False(File.Exists(plan[0].Destination));
    }
    [Fact] public void CollisionsWithinBatchGetDistinctNames()
    {
        var a = Write("a/p.jpg"); var b = Write("b/p.jpg");
        var plan = _service.Plan(new[] { new MoveRequest(a, null), new MoveRequest(b, null) }, Path.Combine(_root, "out"), CollisionPolicy.Rename);
        Assert.NotEqual(plan[0].Destination, plan[1].Destination); Assert.EndsWith("p (1).jpg", plan[1].Destination);
    }
    [Fact] public void YearPlanSeparatesMissingCaptureDate()
    {
        var a = Write("a/p.jpg"); var b = Write("b/p.jpg");
        var plan = _service.Plan(new[] { new MoveRequest(a, new DateTime(2026,9,15)), new MoveRequest(b, null) }, Path.Combine(_root,"out"), CollisionPolicy.Skip, true);
        Assert.Contains(Path.Combine("2026", "09 Сентябрь"), plan[0].Destination); Assert.Contains("Без даты съёмки", plan[1].Destination);
    }
    [Fact] public async Task CancellationBeforeStartPreservesOriginal()
    {
        var source = Write("in/p.jpg"); var plan = _service.Plan(new[] { new MoveRequest(source, null) }, Path.Combine(_root,"out"), CollisionPolicy.Skip);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ExecuteAsync(plan, Journal, _ => Task.CompletedTask, null, cancellation.Token));
        Assert.True(File.Exists(source)); Assert.False(File.Exists(plan[0].Destination));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
