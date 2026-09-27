using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Diagnostics;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class StoragePrivacyPolicyTests
{
    [Theory]
    [InlineData(@"C:\Users\User\OneDrive\Photos")]
    [InlineData(@"D:\Dropbox\Photos")]
    [InlineData(@"\\server\share\Photos")]
    [InlineData("/users/test/Yandex.Disk.localized/Photos")]
    public void KnownSyncLocationsShowAWarning(string path) => Assert.NotNull(StoragePrivacyPolicy.KnownSynchronizationWarning(path));

    [Fact]
    public void QuarantineMembershipUsesDirectoryBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-1");
        Assert.True(StoragePrivacyPolicy.IsUnder(Path.Combine(root, "photo.jpg"), root));
        Assert.False(StoragePrivacyPolicy.IsUnder(Path.Combine(root + "-other", "photo.jpg"), root));
    }

    [Fact]
    public void SupportExportHasAnExplicitAllowlistAndRejectsPathLikeVersionInput()
    {
        var summary = SupportSnapshot.Create("C:\\Users\\Alice\\private-photo.jpg", true, 2);
        Assert.DoesNotContain("Alice", summary); Assert.DoesNotContain("private-photo", summary);
        using var document = System.Text.Json.JsonDocument.Parse(summary);
        var root = document.RootElement;
        Assert.Equal("unknown", root.GetProperty("version").GetString());
        Assert.False(root.GetProperty("originalMediaIncluded").GetBoolean());
        Assert.False(root.GetProperty("filePathsIncluded").GetBoolean());
        Assert.False(root.GetProperty("rawLogsIncluded").GetBoolean());
        Assert.Equal(2, root.GetProperty("currentSessionErrors").GetInt32());
    }
}
