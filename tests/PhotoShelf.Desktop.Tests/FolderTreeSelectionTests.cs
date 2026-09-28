using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Desktop;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

public sealed class FolderTreeSelectionTests
{
    [Fact]
    public Task CheckboxClickUpdatesNodeAndAncestorsImmediatelyWithoutSelectingSibling() => OnStaAsync(() =>
    {
        var rules = new FolderInclusionRules([@"C:\"]);
        var drive = new FolderNode(@"C:\");
        var parent = new FolderNode(@"C:\PhotoShelf-synthetic", drive);
        var wanted = new FolderNode(@"C:\PhotoShelf-synthetic\Wanted", parent);
        var sibling = new FolderNode(@"C:\PhotoShelf-synthetic\Sibling", parent);
        foreach (var node in new[] { drive, parent, wanted, sibling }) node.ApplyInclusion(rules);
        var checkbox = new ClickableCheckBox { DataContext = wanted, IsThreeState = true };
        checkbox.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(FolderNode.CheckState)) { Mode = BindingMode.OneWay });
        checkbox.Click += (_, _) => wanted.ToggleInclusion(rules);
        Assert.False(checkbox.IsChecked);
        checkbox.ClickForTest();
        // No dispatcher yield, filesystem fixture or child enumeration precedes these checks.
        Assert.True(checkbox.IsChecked); Assert.Null(drive.CheckState); Assert.Null(parent.CheckState);
        Assert.False(drive.IsIncluded); Assert.False(sibling.CheckState);
        var unloaded = new FolderNode(@"C:\PhotoShelf-synthetic\Unloaded", parent);
        unloaded.ApplyInclusion(rules); Assert.False(unloaded.CheckState);
        var selectedChild = new FolderNode(@"C:\PhotoShelf-synthetic\Wanted\Unloaded", wanted);
        selectedChild.ApplyInclusion(rules); Assert.True(selectedChild.CheckState);
        checkbox.ClickForTest();
        Assert.False(checkbox.IsChecked); Assert.False(drive.CheckState); Assert.False(parent.CheckState);
    });

    [Fact]
    public Task ClickingMixedParentSelectsThatSubtreeAndKeepsExcludedDriveMixed() => OnStaAsync(() =>
    {
        var rules = new FolderInclusionRules([@"C:\"], [@"C:\PhotoShelf-synthetic\Wanted"]);
        var drive = new FolderNode(@"C:\"); var parent = new FolderNode(@"C:\PhotoShelf-synthetic", drive);
        drive.ApplyInclusion(rules); parent.ApplyInclusion(rules);
        var checkbox = new ClickableCheckBox { DataContext = parent, IsThreeState = true };
        checkbox.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(FolderNode.CheckState)) { Mode = BindingMode.OneWay });
        checkbox.Click += (_, _) => parent.ToggleInclusion(rules);
        Assert.Null(checkbox.IsChecked);
        checkbox.ClickForTest();
        Assert.True(checkbox.IsChecked); Assert.Null(drive.CheckState); Assert.False(drive.IsIncluded);
        Assert.True(rules.IsIncluded(@"C:\PhotoShelf-synthetic\Future child"));
        Assert.False(rules.IsIncluded(@"C:\Other"));
    });

    [Fact]
    public async Task ScannerTraversesExcludedAncestorOnlyToReachIncludedDescendants()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoshelf-tree-scan-" + Guid.NewGuid().ToString("N"));
        var files = new[] { "outside.jpg", "Wanted/selected.jpg", "Wanted/Excluded/outside.jpg", "Wanted/Excluded/Again/selected.jpg", "Sibling/outside.jpg" }
            .Select(relative => Path.GetFullPath(Path.Combine(root, relative))).ToArray();
        try
        {
            foreach (var file in files) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllTextAsync(file, file); }
            var rules = new FolderInclusionRules([root]);
            rules.SetIncluded(Path.Combine(root, "Wanted"), true);
            rules.SetIncluded(Path.Combine(root, "Wanted", "Excluded"), false);
            rules.SetIncluded(Path.Combine(root, "Wanted", "Excluded", "Again"), true);
            var found = new List<string>();
            await PhotoScanner.ScanAsync([root], batch => { found.AddRange(batch.Paths); return Task.CompletedTask; },
                includeSystemFolders: true, CancellationToken.None, inclusion: rules);
            Assert.Equal(new[] { files[1], files[3] }.Order(), found.Order());
            foreach (var file in files) Assert.Equal(file, await File.ReadAllTextAsync(file));
            Assert.Equal(files.Length, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class ClickableCheckBox : CheckBox
    {
        public void ClickForTest() => OnClick();
    }
    private static Task OnStaAsync(Action body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(() =>
            {
                try { body(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
