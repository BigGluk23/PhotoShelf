using PhotoShelf.Application.Catalog;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class FolderInclusionRulesTests
{
    [Fact]
    public void SelectingChildOfDeselectedDriveDoesNotSelectDriveOrSiblings()
    {
        var drive = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        var wanted = Path.Combine(drive, "PhotoShelf-synthetic", "Wanted");
        var sibling = Path.Combine(drive, "PhotoShelf-synthetic", "Sibling");
        var rules = new FolderInclusionRules([]);
        rules.SetIncluded(drive, false);
        Assert.False(rules.IsIncluded(wanted));

        rules.SetIncluded(wanted, true);

        Assert.True(rules.IsIncluded(wanted));
        Assert.False(rules.IsIncluded(drive));
        Assert.False(rules.IsIncluded(sibling));
        Assert.True(rules.IsIncluded(Path.Combine(wanted, "Unloaded child")));
        Assert.Null(rules.GetCheckState(drive));
        Assert.True(rules.GetCheckState(wanted));
        Assert.False(rules.GetCheckState(sibling));
        Assert.True(rules.MayContainIncluded(drive));
        Assert.False(rules.MayContainIncluded(sibling));
    }

    private static string Folder(params string[] parts) => Path.Combine(new[] { Path.GetTempPath(), "photoshelf-rule-test" }.Concat(parts).ToArray());

    [Fact]
    public void RulesSurviveRoundTripWithNestedExceptionsAndUnloadedChildren()
    {
        var root = Folder(); var selected = Folder("Selected"); var excluded = Folder("Selected", "Excluded");
        var restored = Folder("Selected", "Excluded", "Restored");
        var original = new FolderInclusionRules([]);
        original.SetIncluded(root, false); original.SetIncluded(selected, true);
        original.SetIncluded(excluded, false); original.SetIncluded(restored, true);
        var rules = new FolderInclusionRules(original.ExcludedFolders, original.IncludedFolders);
        Assert.Null(rules.GetCheckState(root)); Assert.Null(rules.GetCheckState(selected)); Assert.Null(rules.GetCheckState(excluded));
        Assert.True(rules.GetCheckState(restored));
        Assert.False(rules.IsIncluded(Folder("Selected", "Excluded", "Unknown future child")));
        Assert.True(rules.IsIncluded(Folder("Selected", "Excluded", "Restored", "Future child")));
        Assert.False(rules.IsIncluded(Folder("Sibling")));
    }

    [Fact]
    public void CheckingMixedParentAppliesOnlyThatSubtreeAndUncheckingRestoresAncestorExclusion()
    {
        var root = Folder(); var branch = Folder("Branch"); var child = Folder("Branch", "Child");
        var sibling = Folder("Other"); var rules = new FolderInclusionRules([root]);
        rules.SetIncluded(child, true); rules.SetIncluded(sibling, true);
        Assert.Null(rules.GetCheckState(branch));
        rules.SetIncluded(branch, true);
        Assert.True(rules.GetCheckState(branch)); Assert.True(rules.IsIncluded(sibling)); Assert.False(rules.IsIncluded(root));
        Assert.DoesNotContain(child, rules.IncludedFolders);
        rules.SetIncluded(branch, false);
        Assert.False(rules.GetCheckState(branch)); Assert.True(rules.IsIncluded(sibling));
        rules.SetIncluded(sibling, false);
        Assert.False(rules.GetCheckState(root)); Assert.Empty(rules.IncludedFolders);
    }

    [Fact]
    public void LegacyExclusionsAndBoundaryMatchingKeepExistingMeaning()
    {
        var root = Folder(); var excluded = Folder("Photo"); var rules = new FolderInclusionRules([excluded]);
        Assert.True(rules.IsIncluded(root)); Assert.False(rules.IsIncluded(Folder("Photo", "Child")));
        Assert.True(rules.IsIncluded(Folder("Photographs"))); Assert.Null(rules.GetCheckState(root));
        Assert.False(rules.IsIncluded(excluded.ToUpperInvariant() + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void ExactRuleConflictExcludesButMoreSpecificIncludeStillWorks()
    {
        var root = Folder(); var child = Folder("Child");
        var rules = new FolderInclusionRules([root], [root.ToUpperInvariant(), child]);
        Assert.False(rules.IsIncluded(root)); Assert.True(rules.IsIncluded(child)); Assert.Null(rules.GetCheckState(root));
    }

    [Fact]
    public void BackgroundSnapshotDoesNotChangeDuringRapidToggles()
    {
        var root = Folder(); var child = Folder("Child"); var sibling = Folder("Sibling");
        var rules = new FolderInclusionRules([root]); rules.SetIncluded(child, true);
        var snapshot = rules.Snapshot();
        rules.SetIncluded(child, false); rules.SetIncluded(sibling, true);
        Assert.True(snapshot.IsIncluded(child)); Assert.False(snapshot.IsIncluded(sibling));
        Assert.False(rules.IsIncluded(child)); Assert.True(rules.IsIncluded(sibling));
    }
}
