using PhotoShelf.Application.Diagnostics;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class StartupCheckArgumentsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("photoshelf-check-args-").FullName;

    [Fact]
    public void OrdinaryStartupNeedsNoDiagnosticPaths()
    {
        var options = StartupCheckArguments.Parse(Array.Empty<string>());
        Assert.Equal(StartupCheckKind.None, options.Kind);
        Assert.Null(options.ReportPath);
    }

    [Theory]
    [InlineData("--ui-smoke", "--smoke-report", StartupCheckKind.UserInterface)]
    [InlineData("--ui-browse-smoke", "--smoke-report", StartupCheckKind.UserInterfaceBrowse)]
    [InlineData("--verify-startup-resources", "--startup-report", StartupCheckKind.Resources)]
    public void OnlyKnownModesAcceptANewAbsoluteReport(string mode, string flag, StartupCheckKind kind)
    {
        var report = Path.Combine(_root, "new-report.json");
        var options = StartupCheckArguments.Parse(new[] { mode, flag, report });
        Assert.Equal(kind, options.Kind);
        Assert.Equal(report, options.ReportPath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root)); // Parsing does not write files.
    }

    [Fact]
    public void ExistingFileIsRejectedWithoutChangingItsContents()
    {
        var path = Path.Combine(_root, "important.jpg");
        File.WriteAllText(path, "original bytes");
        Assert.Throws<ArgumentException>(() => StartupCheckArguments.Parse(new[] { "--ui-smoke", "--smoke-report", path }));
        Assert.Equal("original bytes", File.ReadAllText(path));
    }

    [Fact]
    public void DirectoryRelativePathAndMissingParentAreRejected()
    {
        foreach (var path in new[] { _root, "relative.json", Path.Combine(_root, "missing", "report.json") })
            Assert.Throws<ArgumentException>(() => StartupCheckArguments.Parse(new[] { "--ui-smoke", "--smoke-report", path }));
    }

    [Fact]
    public void MissingConflictingAndCallerSelectedCatalogArgumentsAreRejected()
    {
        var path = Path.Combine(_root, "report.json");
        foreach (var args in new[]
        {
            new[] { "--ui-smoke" },
            new[] { "--ui-smoke", "--startup-report", path },
            new[] { "--verify-startup-resources", "--smoke-report", path },
            new[] { "--ui-smoke", "--smoke-report", path, "--catalog", _root },
            new[] { "--ui-browse-smoke", "--startup-report", path },
            new[] { "--ui-browse-smoke", "--smoke-report", path, "--catalog", _root },
            new[] { "--unknown", "--smoke-report", path }
        })
            Assert.Throws<ArgumentException>(() => StartupCheckArguments.Parse(args));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
