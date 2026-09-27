using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PhotoShelf.Application.Files;
using Xunit;
using Xunit.Sdk;

namespace PhotoShelf.Application.Tests;

public sealed class FileMoveProcessCrashTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "photoshelf-crash-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "in", "sample.jpg");
    private string Destination => Path.Combine(_root, "out", "sample.jpg");
    private string Journal => Path.Combine(_root, "operations", "move.jsonl");
    private string CatalogMarker => Path.Combine(_root, "catalog-committed.txt");

    public FileMoveProcessCrashTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        var contents = new byte[256 * 1024];
        new Random(9731).NextBytes(contents);
        File.WriteAllBytes(Source, contents);
        File.SetLastWriteTimeUtc(Source, new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("staged", "copy", false)]
    [InlineData("copy_verified", "copy", false)]
    [InlineData("destination_verified", "copy", false)]
    [InlineData("catalog_committed", "copy", true)]
    [InlineData("source_removed", "copy", true)]
    [InlineData("destination_verified", "rename", false)]
    public Task AbruptProcessDeathRecoversOriginalBytes(string checkpoint, string mode, bool catalogAlreadyCommitted) =>
        KillAndRecoverAsync(checkpoint, mode, catalogAlreadyCommitted);

    [WindowsFact] public Task AbruptWindowsProcessDeathWhileNativeCleanupHandleIsLockedRecoversOriginalBytes() =>
        KillAndRecoverAsync("source_locked_for_cleanup", "copy", true);

    [WindowsFact] public Task AbruptWindowsProcessDeathAfterNativeSourceDeletionRecoversOriginalBytes() =>
        KillAndRecoverAsync("source_removed", "copy", true);

    private async Task KillAndRecoverAsync(string checkpoint, string mode, bool catalogAlreadyCommitted)
    {
        var originalHash = Hash(Source);
        var probe = Path.Combine(AppContext.BaseDirectory, "CrashProbe", "PhotoShelf.CrashProbe.dll");
        Assert.True(File.Exists(probe), $"Crash probe was not built/copied: {probe}");
        var start = new ProcessStartInfo(FindDotnetHost())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _root
        };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(_root);
        start.ArgumentList.Add(checkpoint);
        start.ArgumentList.Add(mode);
        using var process = Process.Start(start) ?? throw new XunitException("Failed to start isolated crash probe");
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            string? reached;
            try { reached = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (TimeoutException)
            {
                await TerminateAsync(process);
                throw new XunitException($"Crash probe did not reach {checkpoint} within 12 seconds. {await stderr}");
            }
            if (reached != "CHECKPOINT:" + checkpoint)
            {
                await TerminateAsync(process);
                throw new XunitException($"Crash probe did not reach {checkpoint}: {reached}. {await stderr}");
            }
            Assert.False(process.HasExited, "The process must still hold the operation handles when killed");
            await TerminateAsync(process);
            Assert.NotEqual(0, process.ExitCode);
            Assert.Equal(catalogAlreadyCommitted, File.Exists(CatalogMarker));
            Assert.Contains(MediaCandidates(), path => Hash(path) == originalHash);

            var callbacks = 0;
            var service = new FileMoveService(new FileMoveOptions { AlwaysCopy = mode == "copy" });
            var recovered = await service.RecoverAsync(Journal, entry =>
            {
                Assert.Equal(originalHash, Hash(entry.Destination));
                // Mock a durable idempotent catalog reconciliation, including an uncertain prior commit.
                if (!File.Exists(CatalogMarker))
                {
                    using var marker = new FileStream(CatalogMarker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    marker.Write([1]); marker.Flush(true);
                }
                callbacks++;
                return Task.CompletedTask;
            }, null, CancellationToken.None);
            Assert.True(Assert.Single(recovered).Moved, recovered[0].Error);
            Assert.Equal(1, callbacks);
            Assert.Equal(originalHash, Hash(Destination));
            Assert.False(File.Exists(Source));
            Assert.Empty(Directory.GetFiles(_root, "*.photoshelf-moving-*", SearchOption.AllDirectories));
            Assert.True(File.Exists(CatalogMarker));
            if (OperatingSystem.IsWindows())
                Assert.Empty(Directory.GetFiles(_root, "*.photoshelf-retained-*", SearchOption.AllDirectories));
            else
                Assert.All(Directory.GetFiles(_root, "*.photoshelf-retained-*", SearchOption.AllDirectories), path => Assert.Equal(originalHash, Hash(path)));

            var repeated = await service.RecoverAsync(Journal,
                _ => throw new XunitException("Completed recovery must not execute another filesystem/catalog operation"), null, CancellationToken.None);
            Assert.True(Assert.Single(repeated).Moved);
            Assert.Equal(originalHash, Hash(Destination));
            Assert.Equal(0, Assert.Single(service.ReadHistory(Path.GetDirectoryName(Journal)!)).PendingFiles);
        }
        finally { await TerminateAsync(process); }
    }

    private IEnumerable<string> MediaCandidates() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetDirectoryName(path) != Path.Combine(_root, "operations") && path != CatalogMarker);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static async Task TerminateAsync(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    private static string FindDotnetHost()
    {
        var environmentHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(environmentHost) && File.Exists(environmentHost)) return environmentHost;
        var processPath = Environment.ProcessPath;
        if (processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && File.Exists(processPath)) return processPath;
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var host = Path.Combine(runtime.Parent?.Parent?.Parent?.FullName ?? "", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (File.Exists(host)) return host;
        throw new XunitException("No usable dotnet host found. Set DOTNET_HOST_PATH; crash tests cannot be silently skipped.");
    }
    public void Dispose() => Directory.Delete(_root, true);
}
