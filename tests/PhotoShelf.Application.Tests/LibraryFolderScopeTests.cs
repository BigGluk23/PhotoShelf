using PhotoShelf.Application.Catalog;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class LibraryFolderScopeTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "photoshelf-scope");

    [Fact]
    public void ExcludedAncestorWatchesOnlyExplicitlyIncludedChildren()
    {
        var child = Path.Combine(Root, "photos");
        var nested = Path.Combine(child, "videos");
        var rules = new FolderInclusionRules([Root], [child]);
        Assert.Equal([child], LibraryFolderScope.IncludedRoots([Root, child, nested], rules));
        Assert.False(rules.IsIncluded(Path.Combine(Root, "unrelated")));
    }

    [Fact]
    public void IncludedGrandchildSurvivesExcludedParentAndUnwatchedRootsStayUnwatched()
    {
        var child = Path.Combine(Root, "excluded");
        var included = Path.Combine(child, "included");
        Assert.Equal([included], LibraryFolderScope.IncludedRoots([Root], new([Root, child], [included])));
        Assert.Empty(LibraryFolderScope.IncludedRoots([], new([])));
    }

    [Fact]
    public void NonrecursiveScopeAcceptsDirectFilesButNotNestedFilesOrSiblingPrefixes()
    {
        Assert.True(LibraryFolderScope.Contains(Path.Combine(Root, "direct.png"), Root, false));
        Assert.True(LibraryFolderScope.Contains(Root, Root, false));
        Assert.False(LibraryFolderScope.Contains(Path.Combine(Root, "child", "nested.png"), Root, false));
        Assert.True(LibraryFolderScope.Contains(Path.Combine(Root, "child", "nested.png"), Root, true));
        Assert.False(LibraryFolderScope.Contains(Path.Combine(Root + "-other", "photo.png"), Root, true));
    }
}
