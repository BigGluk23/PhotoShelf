using PhotoShelf.Application.Files;
using PhotoShelf.Domain;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class FileMoveCompanionRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "photoshelf-companion-" + Guid.NewGuid().ToString("N"));
    public static IEnumerable<object[]> RawCases => MediaFormatRegistry.RawExtensions.SelectMany(extension => new[] { new object[] { extension, false }, new object[] { extension, true } });
    private string Write(string folder, string name, string content)
    {
        var path = Path.GetFullPath(Path.Combine(_root, folder, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path;
    }

    [Theory, MemberData(nameof(RawCases))]
    public async Task EveryCatalogRawStaysWithJpegAndSidecarThroughRenameQuarantineAndUndo(string extension, bool selectJpeg)
    {
        var raw = Write("source", "shot" + extension, "unique raw bytes");
        var jpeg = Write("source", "shot.jpg", "jpeg bytes");
        var sidecar = Write("source", "shot.xmp", "unique shared metadata");
        var existing = Write("quarantine", "shot.jpg", "unrelated occupied target");
        var originals = new Dictionary<string, string> { [raw] = "unique raw bytes", [jpeg] = "jpeg bytes", [sidecar] = "unique shared metadata" };
        var service = new FileMoveService();
        var plan = service.Plan([new(selectJpeg ? jpeg : raw, null)], Path.GetDirectoryName(existing)!, CollisionPolicy.Rename);
        Assert.Equal(3, plan.Count); Assert.All(plan, entry => Assert.Null(entry.SkipReason));
        Assert.Single(plan.Select(entry => entry.GroupId).Distinct());
        Assert.All(plan, entry => Assert.StartsWith("shot (1).", Path.GetFileName(entry.Destination)));
        var journal = Path.Combine(_root, "operations", "move.jsonl");
        Assert.All(await service.ExecuteAsync(plan, journal, _ => Task.CompletedTask, null, default), result => Assert.True(result.Moved, result.Error));
        foreach (var entry in plan) { Assert.False(File.Exists(entry.Source)); Assert.Equal(originals[entry.Source], File.ReadAllText(entry.Destination)); }
        Assert.Equal("unrelated occupied target", File.ReadAllText(existing));
        var undo = await service.UndoAsync(journal, Path.Combine(_root, "operations", "undo.jsonl"), _ => Task.CompletedTask, null, default);
        Assert.Equal(3, undo.Count); Assert.All(undo, result => Assert.True(result.Moved, result.Error));
        foreach (var original in originals) Assert.Equal(original.Value, File.ReadAllText(original.Key));
        Assert.Equal("unrelated occupied target", File.ReadAllText(existing));
    }

    [Theory]
    [InlineData(".heic")]
    [InlineData(".heif")]
    [InlineData(".hif")]
    public void IphoneImageAndMovRequireConfirmedRelationshipInEitherDirection(string extension)
    {
        var image = Write("source", "shot" + extension, "still");
        var mov = Write("source", "shot.mov", "motion");
        var service = new FileMoveService();
        foreach (var source in new[] { image, mov })
            Assert.All(service.Plan([new(source, null)], Path.Combine(_root, "out"), CollisionPolicy.Skip), entry => Assert.NotNull(entry.SkipReason));
        var confirmed = service.Plan([new(image, null, [mov])], Path.Combine(_root, "out"), CollisionPolicy.Skip);
        Assert.Equal(2, confirmed.Count); Assert.All(confirmed, entry => Assert.Null(entry.SkipReason));
    }

    [Fact]
    public async Task UnknownSameStemPreventsAutomaticSplitWithoutChangingAnyFile()
    {
        var photo = Write("source", "shot.jpg", "photo");
        var unknown = Write("source", "shot.future-raw", "unique not-yet-supported data");
        var xmp = Write("source", "shot.xmp", "metadata");
        var service = new FileMoveService();
        var plan = service.Plan([new(photo, null)], Path.Combine(_root, "out"), CollisionPolicy.Skip);
        Assert.All(plan, entry => Assert.NotNull(entry.SkipReason));
        Assert.All(await service.ExecuteAsync(plan, Path.Combine(_root, "move.jsonl"), _ => throw new Exception("Must not commit"), null, default), result => Assert.False(result.Moved));
        Assert.Equal("photo", File.ReadAllText(photo)); Assert.Equal("unique not-yet-supported data", File.ReadAllText(unknown)); Assert.Equal("metadata", File.ReadAllText(xmp));
        var confirmed = service.Plan([new(photo, null, [unknown])], Path.Combine(_root, "out"), CollisionPolicy.Skip);
        Assert.Equal(3, confirmed.Count); Assert.All(confirmed, entry => Assert.Null(entry.SkipReason));
    }

    [Fact]
    public void ExtensionSpecificAaeAndXmpRemainWithBothPrimaryMembers()
    {
        var raw = Write("source", "shot.rwl", "raw"); var jpeg = Write("source", "shot.jpg", "jpeg");
        var aae = Write("source", "shot.jpg.aae", "edits"); var xmp = Write("source", "shot.rwl.xmp", "raw metadata");
        var plan = new FileMoveService().Plan([new(jpeg, null)], Path.Combine(_root, "out"), CollisionPolicy.Skip);
        Assert.Equal(new[] { raw, jpeg, aae, xmp }.Order(StringComparer.Ordinal), plan.Select(entry => entry.Source).Order(StringComparer.Ordinal));
        Assert.All(plan, entry => Assert.Null(entry.SkipReason));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
