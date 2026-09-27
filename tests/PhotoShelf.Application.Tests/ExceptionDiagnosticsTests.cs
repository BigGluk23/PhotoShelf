using System.Globalization;
using System.Runtime.InteropServices;
using PhotoShelf.Application.Diagnostics;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class ExceptionDiagnosticsTests
{
    [Fact]
    public void NestedExceptionKeepsOriginalStackAndExposesUnderlyingCause()
    {
        var root = Assert.Throws<FileNotFoundException>(ThrowMissingStartupAsset);
        var wrapped = new InvalidOperationException("TypeConverter could not provide a value.",
            new FormatException("Cannot load window icon.", root));

        var diagnostic = ExceptionDiagnostics.Create(wrapped, "startup", "0.10.1-ultra");

        Assert.Equal("System.IO.FileNotFoundException: Missing embedded icon.", diagnostic.RootCause);
        Assert.Contains(wrapped.ToString(), diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowMissingStartupAsset), diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains("System.FormatException", diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains(root.StackTrace!, diagnostic.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregatePreservesAllBranchesAndTheirUnderlyingCauses()
    {
        var first = new FileNotFoundException("Missing icon.");
        var second = new IOException("Catalog cannot be opened.");
        var aggregate = new AggregateException("Startup tasks failed.",
            new InvalidOperationException("Resource initialization failed.", first),
            new AggregateException("Catalog initialization failed.", second));

        var diagnostic = ExceptionDiagnostics.Create(aggregate, "initialization", "0.10.1-ultra");

        Assert.Contains("System.IO.FileNotFoundException: Missing icon.", diagnostic.RootCause, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException: Catalog cannot be opened.", diagnostic.RootCause, StringComparison.Ordinal);
        Assert.Contains(aggregate.ToString(), diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains("Resource initialization failed.", diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains("Catalog initialization failed.", diagnostic.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportIdentifiesContextVersionPlatformAndUtcTime()
    {
        var before = DateTimeOffset.UtcNow;
        var diagnostic = ExceptionDiagnostics.Create(new InvalidOperationException("Test failure."),
            "MainWindow.InitializeComponent", "0.10.1-ultra");
        var after = DateTimeOffset.UtcNow;

        Assert.Contains("Context: MainWindow.InitializeComponent", diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains("Version: 0.10.1-ultra", diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains($"Runtime: {RuntimeInformation.FrameworkDescription}", diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains($"OS: {RuntimeInformation.OSDescription}", diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains($"OS architecture: {RuntimeInformation.OSArchitecture}", diagnostic.Details, StringComparison.Ordinal);
        Assert.Contains($"Process architecture: {RuntimeInformation.ProcessArchitecture}", diagnostic.Details, StringComparison.Ordinal);
        var timestampLine = diagnostic.Details.Split(Environment.NewLine).Single(line => line.StartsWith("UTC: ", StringComparison.Ordinal));
        var timestamp = DateTimeOffset.ParseExact(timestampLine[5..], "O", CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
        Assert.InRange(timestamp, before, after);
    }

    private static void ThrowMissingStartupAsset() => throw new FileNotFoundException("Missing embedded icon.");
}
