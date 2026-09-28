using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PhotoShelf.Application.Media;
using Xunit;
using Xunit.Sdk;

namespace PhotoShelf.Application.Tests;

[CollectionDefinition("HEIF process environment", DisableParallelization = true)]
public sealed class HeifProcessEnvironmentCollection { }

[Collection("HEIF process environment")]
public sealed class HeifDecoderClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-heif-client-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "original.heic");
    public HeifDecoderClientTests()
    {
        Directory.CreateDirectory(_root);
        var bytes = new byte[32 * 1024]; new Random(2741).NextBytes(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 24); "ftypheic"u8.CopyTo(bytes.AsSpan(4));
        bytes.AsSpan(12, 4).Clear(); "mif1heic"u8.CopyTo(bytes.AsSpan(16));
        File.WriteAllBytes(Source, bytes); File.SetLastWriteTimeUtc(Source, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    private HeifDecoderClient Client(string mode, string report, TimeSpan? timeout = null)
    {
        var probe = Path.Combine(AppContext.BaseDirectory, "HeifProbe", "PhotoShelf.HeifProbe.dll");
        Assert.True(File.Exists(probe), $"HEIF fake probe was not built/copied: {probe}");
        return new(FindDotnetHost(), new[] { probe, "--probe-mode", mode, "--probe-report", report }, timeout);
    }
    private string Report(string name = "worker") => Path.Combine(_root, name + ".json");
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void AssertDead(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.WaitForExit(0), "Worker still has a live process handle after DecodeAsync completed."); }
        catch (ArgumentException) { /* OS no longer exposes that process. */ }
    }
    private static JsonDocument ReadReport(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static async Task WaitForReportAsync(string report, Task work)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(report))
        {
            if (work.IsCompleted) { await work; throw new XunitException("Worker completed without its synthetic report."); }
            if (timer.Elapsed > TimeSpan.FromSeconds(12)) throw new XunitException("Synthetic worker did not start within 12 seconds.");
            await Task.Delay(25);
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("stderr-flood")]
    public async Task ValidBoundedFrameStreamsOriginalBytesWithoutExposingItsPathOrChangingIt(string mode)
    {
        var hash = Hash(Source); var modified = File.GetLastWriteTimeUtc(Source); var report = Report();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var frame = await Client(mode, report).DecodeAsync(Source, 256, cancellation.Token);
        Assert.Equal(2, frame.Width); Assert.Equal(1, frame.Height); Assert.Equal(8, frame.Stride);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 }, frame.Pixels);
        using var data = ReadReport(report);
        Assert.Equal(new FileInfo(Source).Length, data.RootElement.GetProperty("inputBytes").GetInt64());
        Assert.Equal(hash, data.RootElement.GetProperty("inputHash").GetString());
        Assert.DoesNotContain(Source, data.RootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()));
        AssertDead(data.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(hash, Hash(Source)); Assert.Equal(modified, File.GetLastWriteTimeUtc(Source));
        using var exclusive = new FileStream(Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("bad-header")]
    [InlineData("truncated")]
    [InlineData("oversize")]
    [InlineData("zero-height")]
    [InlineData("bad-stride")]
    [InlineData("trailing")]
    [InlineData("nonzero")]
    public async Task InvalidWorkerOutputFailsClosedAndLeavesOriginalReadable(string mode)
    {
        var hash = Hash(Source); var report = Report();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var error = await Record.ExceptionAsync(() => Client(mode, report).DecodeAsync(Source, 256, cancellation.Token));
        Assert.True(error is IOException or InvalidDataException, $"Expected protocol/I/O failure, got {error}");
        using var data = ReadReport(report); AssertDead(data.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(hash, Hash(Source));
        using var exclusive = new FileStream(Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task TimeoutKillsAndWaitsForTheActualWorkerBeforeReleasingTheInput()
    {
        var hash = Hash(Source); var report = Report(); var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => Client("timeout", report, TimeSpan.FromSeconds(3)).DecodeAsync(Source, 256));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(12));
        using var data = ReadReport(report); AssertDead(data.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(hash, Hash(Source));
        using var exclusive = new FileStream(Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task CancellationTerminatesTheWorkerAndQueuedRequestsCannotStartAnotherProcess()
    {
        var hash = Hash(Source); var firstReport = Report("first"); var secondReport = Report("second");
        using var firstCancel = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var secondCancel = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var first = Client("timeout", firstReport).DecodeAsync(Source, 256, firstCancel.Token);
        Task<RgbaFrame>? second = null;
        try
        {
            await WaitForReportAsync(firstReport, first);
            second = Client("valid", secondReport).DecodeAsync(Source, 256, secondCancel.Token);
            await Task.Delay(100); Assert.False(File.Exists(secondReport));
            secondCancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            firstCancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            using var data = ReadReport(firstReport); AssertDead(data.RootElement.GetProperty("pid").GetInt32());
            Assert.False(File.Exists(secondReport)); Assert.Equal(hash, Hash(Source));
            Assert.Equal(8, (await Client("valid", Report("third")).DecodeAsync(Source, 256)).Pixels.Length);
        }
        finally
        {
            firstCancel.Cancel(); secondCancel.Cancel();
            try { await first; } catch { }
            if (second is not null) try { await second; } catch { }
        }
    }

    [WindowsFact]
    public async Task WindowsJobCapsAreAppliedBeforeInputAndTheOriginalRemainsLockedWhileWorkerIsAlive()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25)); var report = Report();
        var decode = Client("timeout", report).DecodeAsync(Source, 256, cancellation.Token);
        try
        {
            await WaitForReportAsync(report, decode); using var data = ReadReport(report);
            Assert.Equal(0x2508u, data.RootElement.GetProperty("jobFlags").GetUInt32());
            Assert.Equal(1u, data.RootElement.GetProperty("jobProcesses").GetUInt32());
            Assert.Equal(1024UL * 1024 * 1024, data.RootElement.GetProperty("jobMemory").GetUInt64());
            Assert.Throws<IOException>(() => { using var locked = new FileStream(Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
            cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decode);
            AssertDead(data.RootElement.GetProperty("pid").GetInt32());
            using var exclusive = new FileStream(Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { cancellation.Cancel(); try { await decode; } catch { } }
    }

    [Fact]
    public async Task DangerousInheritedHeifEnvironmentIsNotForwarded()
    {
        var oldLimits = Environment.GetEnvironmentVariable("LIBHEIF_SECURITY_LIMITS");
        var oldPlugins = Environment.GetEnvironmentVariable("LIBHEIF_PLUGIN_PATH");
        try
        {
            Environment.SetEnvironmentVariable("LIBHEIF_SECURITY_LIMITS", "off");
            Environment.SetEnvironmentVariable("LIBHEIF_PLUGIN_PATH", "untrusted-synthetic-plugin-directory");
            var report = Report(); await Client("valid", report).DecodeAsync(Source, 256);
            using var data = ReadReport(report);
            Assert.Equal(JsonValueKind.Null, data.RootElement.GetProperty("securityLimits").ValueKind);
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(FindDotnetHost())), data.RootElement.GetProperty("pluginPath").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIBHEIF_SECURITY_LIMITS", oldLimits);
            Environment.SetEnvironmentVariable("LIBHEIF_PLUGIN_PATH", oldPlugins);
        }
    }

    [Fact]
    public async Task OversizedInputAndMisleadingExtensionAreRejectedBeforeWorkerStarts()
    {
        var report = Report(); var client = Client("valid", report);
        await File.WriteAllTextAsync(Source, "This is not a HEIF file despite its extension.");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DecodeAsync(Source, 256)); Assert.False(File.Exists(report));
        await using (var file = new FileStream(Source, FileMode.Create, FileAccess.Write)) file.SetLength(HeifDecoderClient.MaxInputBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DecodeAsync(Source, 256)); Assert.False(File.Exists(report));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(4097)]
    public async Task InvalidRequestedDimensionsNeverStartAWorker(int maximum)
    {
        var report = Report();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Client("valid", report).DecodeAsync(Source, maximum));
        Assert.False(File.Exists(report));
    }

    [Fact]
    public void GenericHeifBrandsDoNotRouteAvifToTheHevcWorker()
    {
        var bytes = File.ReadAllBytes(Source);
        "mif1"u8.CopyTo(bytes.AsSpan(8)); "mif1msf1"u8.CopyTo(bytes.AsSpan(16));
        using (var generic = new MemoryStream(bytes)) Assert.True(HeifDecoderClient.HasHeifSignature(generic));
        "avif"u8.CopyTo(bytes.AsSpan(8));
        using (var primaryAvif = new MemoryStream(bytes)) Assert.False(HeifDecoderClient.HasHeifSignature(primaryAvif));
        "mif1"u8.CopyTo(bytes.AsSpan(8)); "avis"u8.CopyTo(bytes.AsSpan(20));
        using (var compatibleAvif = new MemoryStream(bytes)) Assert.False(HeifDecoderClient.HasHeifSignature(compatibleAvif));
        "heic"u8.CopyTo(bytes.AsSpan(8));
        using (var explicitHevc = new MemoryStream(bytes)) Assert.True(HeifDecoderClient.HasHeifSignature(explicitHevc));
    }

    [Fact]
    public void SignatureValidationUsesBrandsRatherThanExtensionAndRestoresStreamPosition()
    {
        var bytes = File.ReadAllBytes(Source); using var valid = new MemoryStream(bytes); valid.Position = 7;
        Assert.True(HeifDecoderClient.HasHeifSignature(valid)); Assert.Equal(7, valid.Position);
        "avc1"u8.CopyTo(bytes.AsSpan(8)); "avc1avc1"u8.CopyTo(bytes.AsSpan(16));
        using var other = new MemoryStream(bytes); other.Position = 3;
        Assert.False(HeifDecoderClient.HasHeifSignature(other)); Assert.Equal(3, other.Position);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, uint.MaxValue);
        using var oversized = new MemoryStream(bytes); Assert.False(HeifDecoderClient.HasHeifSignature(oversized)); Assert.Equal(0, oversized.Position);
    }

    private static string FindDotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
        var process = Environment.ProcessPath;
        if (process is not null && Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && File.Exists(process)) return process;
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var host = Path.Combine(runtime.Parent?.Parent?.Parent?.FullName ?? "", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (File.Exists(host)) return host;
        throw new XunitException("No dotnet host for HEIF process tests. Set DOTNET_HOST_PATH; do not skip containment tests.");
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
