using PhotoShelf.Application.Catalog;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class FileSystemObservationProbeTests : IDisposable
{
    private readonly string _root = LibraryChangeCoordinatorTests.RootPath();
    private readonly FileSystemObservationProbe _probe = new();

    public FileSystemObservationProbeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void MissingFileHasNoFabricatedSizeDateOrIdentityAndDirectoryIsDistinguished()
    {
        var missing = _probe.ProbeFile(Path.Combine(_root, "missing.jpg"));
        Assert.Equal(FileAvailability.Missing, missing.Availability);
        Assert.Null(missing.Length); Assert.Null(missing.LastWriteTimeUtc); Assert.Null(missing.FileIdentity);
        var root = _probe.ProbeRoot(_root);
        Assert.Equal(FileAvailability.Available, root.Availability);
        Assert.True(root.IsDirectory);
    }

    [Fact]
    public void AvailableFileIsObservedWithoutChangingItsBytesOrModificationTime()
    {
        var path = Path.Combine(_root, "synthetic.jpg");
        var bytes = new byte[] { 1, 7, 9, 33, 47 };
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var before = File.GetLastWriteTimeUtc(path);
        var result = _probe.ProbeFile(path);
        Assert.Equal(FileAvailability.Available, result.Availability);
        Assert.Equal(bytes.Length, result.Length);
        Assert.Equal(before, result.LastWriteTimeUtc);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        if (!OperatingSystem.IsWindows()) Assert.Null(result.FileIdentity);
    }

    [Fact]
    public void SymbolicLinkIsNotFollowedOnPortablePlatforms()
    {
        if (OperatingSystem.IsWindows()) return; // Windows junction case has its own native test.
        var target = Path.Combine(_root, "target.jpg"); File.WriteAllText(target, "synthetic");
        var link = Path.Combine(_root, "linked.jpg"); File.CreateSymbolicLink(link, target);
        var result = _probe.ProbeFile(link);
        Assert.Equal(FileAvailability.NeedsVerification, result.Availability);
        Assert.Equal("ReparsePoint", result.ErrorCode);
        Assert.Null(result.FileIdentity); Assert.Null(result.Length);
        var folder = Path.Combine(_root, "subfolder"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "child.jpg"), "synthetic");
        var folderLink = Path.Combine(_root, "linked-folder"); Directory.CreateSymbolicLink(folderLink, folder);
        Assert.Equal("ReparsePoint", _probe.ProbeFile(Path.Combine(folderLink, "child.jpg")).ErrorCode);
    }

    [WindowsFact]
    public void WindowsIdentitySurvivesRenameAndIncludesCreationTimeWhileReplacedFileDiffers()
    {
        var first = Path.Combine(_root, "first.jpg"); var second = Path.Combine(_root, "second.jpg");
        File.WriteAllText(first, "synthetic-original");
        var original = _probe.ProbeFile(first);
        Assert.Equal(FileAvailability.Available, original.Availability);
        Assert.StartsWith("win:", original.FileIdentity);
        Assert.EndsWith(original.CreationTimeUtc!.Value.Ticks.ToString("X16"), original.FileIdentity);
        File.Move(first, second);
        Assert.Equal(original.FileIdentity, _probe.ProbeFile(second).FileIdentity);
        File.WriteAllText(first, "synthetic-replacement");
        Assert.NotEqual(original.FileIdentity, _probe.ProbeFile(first).FileIdentity);
    }

    [WindowsFact]
    public void WindowsJunctionAndItsChildrenAreNotFollowed()
    {
        var target = Path.Combine(_root, "target"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "child.jpg"), "synthetic");
        var junction = Path.Combine(_root, "junction");
        using var command = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", junction, target }
        })!;
        Assert.True(command.WaitForExit(5000)); Assert.Equal(0, command.ExitCode);
        Assert.Equal("ReparsePoint", _probe.ProbeRoot(junction).ErrorCode);
        Assert.Equal("ReparsePoint", _probe.ProbeFile(Path.Combine(junction, "child.jpg")).ErrorCode);
        Directory.Delete(junction);
        Assert.Equal("synthetic", File.ReadAllText(Path.Combine(target, "child.jpg")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
