using PhotoShelf.Application.Files;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class JournalEvidenceExportTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "photoshelf-evidence-" + Guid.NewGuid().ToString("N"));
    public JournalEvidenceExportTests() => Directory.CreateDirectory(_root);

    [Fact] public async Task CorruptJournalCanBeCopiedVerbatimWithoutInterpretingOrChangingIt()
    {
        var source = Path.Combine(_root, "broken.jsonl");
        var destination = Path.Combine(_root, "evidence.jsonl");
        byte[] bytes = [0, 255, 123, 10, 42]; await File.WriteAllBytesAsync(source, bytes);
        var hash = await JournalEvidenceExport.CopyAsync(_root, source, destination);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source)); Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), hash);
    }
    [Fact] public async Task ExistingDestinationAndOutsideSourceAreRefused()
    {
        var source = Path.Combine(_root, "broken.jsonl"); var destination = Path.Combine(_root, "existing.jsonl");
        await File.WriteAllTextAsync(source, "source"); await File.WriteAllTextAsync(destination, "existing");
        await Assert.ThrowsAsync<IOException>(() => JournalEvidenceExport.CopyAsync(_root, source, destination));
        Assert.Equal("source", await File.ReadAllTextAsync(source)); Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        await Assert.ThrowsAsync<IOException>(() => JournalEvidenceExport.CopyAsync(Path.Combine(_root,"other"), source, Path.Combine(_root,"new.jsonl")));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
