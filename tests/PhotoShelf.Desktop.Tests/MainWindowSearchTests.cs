using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Infrastructure.Sqlite;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

public sealed class MainWindowSearchTests
{
    [Fact]
    public Task NewRootSearchUsesOneAwaitedMonitorTraversalWithoutAParallelManualScan() => WpfTestDispatcher.RunAsync(async () =>
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var original = await fixture.CreatePngAsync("original.png");
        var before = await Fingerprint.ReadAsync(original);
        using var reader = fixture.BlockNextReconciliation(original);
        var search = fixture.ScanAsync();
        await reader.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        // The production callback has not scanned yet. The former independent
        // PhotoScanner task could commit this file while monitoring was blocked.
        await Task.Delay(600);
        Assert.False(search.IsCompleted);
        Assert.True(reader.ReaderOpen);
        Assert.Null(await fixture.Store.GetItemAsync(original));
        Assert.Equal(0, await fixture.CountAsync());
        Assert.Equal(1, fixture.FullTraversalCount);

        reader.Release();
        await search.WaitAsync(TimeSpan.FromSeconds(15));
        await fixture.WaitForMonitorIdleAsync();
        await Task.Delay(300); // Several worker polls; a duplicate initial pass must not replay.
        Assert.Equal(1, fixture.FullTraversalCount);
        Assert.Equal(1, fixture.Monitor.Activity.CompletedReconciliationCount);
        Assert.NotNull(await fixture.Store.GetItemAsync(original));
        Assert.Equal(1, await fixture.CountAsync());
        Assert.Equal(before, await Fingerprint.ReadAsync(original));
    });

    [Fact]
    public Task StopWaitsForRealReaderAndRetainsEventsUntilExplicitSearchAfterIncidentalConfiguration() => WpfTestDispatcher.RunAsync(async () =>
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var original = await fixture.CreatePngAsync("committed.png");
        var originalBefore = await Fingerprint.ReadAsync(original);
        await fixture.ScanAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await fixture.WaitForMonitorIdleAsync();
        await fixture.Store.SetFavoriteAsync(original, true);
        var committed = (await fixture.Store.GetItemAsync(original))!;
        Assert.False(string.IsNullOrEmpty(committed.AssetId));

        using var reader = fixture.BlockNextReconciliation(original);
        var search = fixture.ScanAsync();
        await reader.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var newFile = await fixture.CreatePngAsync("queued-while-reading.png");
        var newBefore = await Fingerprint.ReadAsync(newFile);
        fixture.Watchers.Emit(newFile);
        Assert.True(fixture.Monitor.Activity.PendingPathCount > 0);

        var stop = InvokeTask(fixture.Window, "StopSearchAsync");
        await reader.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(10));
        // A later programmatic request waits behind the active Stop. Repeating
        // Stop must cancel that request too, without waiting cyclically on it.
        var supersededSearch = fixture.ScanAsync();
        Assert.False(supersededSearch.IsCompleted);
        Assert.Same(stop, InvokeTask(fixture.Window, "StopSearchAsync"));
        Assert.False(stop.IsCompleted);
        Assert.True(reader.ReaderOpen);
        Assert.True(Field<bool>(fixture.Window, "_searchStopping"));
        Assert.True(Field<bool>(fixture.Window, "_searchStopped"));
        var stopButton = (Button)fixture.Window.FindName("CancelScanButton");
        Assert.False(stopButton.IsEnabled);
        Assert.Contains("Останавливаю", Assert.IsType<string>(stopButton.Content));

        // Simulate an incidental tree/settings refresh while stopping. This must
        // neither announce completion nor restart the still-open reader.
        Invoke(fixture.Window, "QueueLibraryMonitoring");
        await Task.Delay(250);
        Assert.False(stop.IsCompleted);
        Assert.True(reader.ReaderOpen);
        Assert.Equal(1, await fixture.CountAsync());
        Assert.Null(await fixture.Store.GetItemAsync(newFile));
        reader.Release();
        await stop.WaitAsync(TimeSpan.FromSeconds(15));
        await search.WaitAsync(TimeSpan.FromSeconds(15));
        await supersededSearch.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(reader.ReaderOpen);
        Assert.False(Field<bool>(fixture.Window, "_searchStopping"));
        Assert.True(Field<bool>(fixture.Window, "_searchStopped"));
        Assert.True(fixture.Monitor.Activity.IsPaused);
        Assert.False(fixture.Monitor.Activity.IsProcessing);
        Assert.False(stopButton.IsEnabled);
        Assert.True(fixture.Monitor.Activity.PendingPathCount > 0);

        // Force a real configuration-key change, rather than testing only a no-op.
        var generation = fixture.Monitor.Generation;
        var traversals = fixture.FullTraversalCount;
        SetField(fixture.Window, "_includeSubfolders", false);
        Invoke(fixture.Window, "QueueLibraryMonitoring");
        await Field<Task>(fixture.Window, "_monitorConfigurationTask").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Monitor.Generation > generation);
        await Task.Delay(600);
        Assert.True(fixture.Monitor.Activity.IsPaused);
        Assert.False(fixture.Monitor.Activity.IsProcessing);
        Assert.Equal(traversals, fixture.FullTraversalCount);
        Assert.True(fixture.Monitor.Activity.PendingPathCount > 0);
        Assert.Null(await fixture.Store.GetItemAsync(newFile));
        await AssertCommittedOriginalAsync(fixture.Store, original, committed, originalBefore);

        // Explicit user search is allowed to clear the Stop guard. The retained
        // event and interrupted traversal then run through the same coordinator.
        await fixture.ScanAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await fixture.WaitForMonitorIdleAsync();
        Assert.False(Field<bool>(fixture.Window, "_searchStopped"));
        Assert.False(fixture.Monitor.Activity.IsPaused);
        Assert.Equal(0, fixture.Monitor.Activity.PendingPathCount);
        Assert.NotNull(await fixture.Store.GetItemAsync(newFile));
        Assert.Equal(2, await fixture.CountAsync());
        await AssertCommittedOriginalAsync(fixture.Store, original, committed, originalBefore);
        Assert.Equal(newBefore, await Fingerprint.ReadAsync(newFile));
    });

    [Fact]
    public Task StopBeforeCheckboxDebouncePreservesSelectedRootWithoutStartingItsScan() => WpfTestDispatcher.RunAsync(async () =>
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var original = await fixture.CreatePngAsync("checked-folder.png");
        var before = await Fingerprint.ReadAsync(original);
        var rules = Field<FolderInclusionRules>(fixture.Window, "_folderInclusion");
        rules.SetIncluded(fixture.Root, false);
        var node = new FolderNode(fixture.Root);
        node.ApplyInclusion(rules);
        var checkbox = new CheckBox { DataContext = node };

        Invoke(fixture.Window, "OnFolderIncludedChanged", checkbox, new RoutedEventArgs(ButtonBase.ClickEvent, checkbox));
        Assert.True(node.CheckState);
        Assert.Contains(fixture.Root, Field<HashSet<string>>(fixture.Window, "_pendingFolderScans"));
        await InvokeTask(fixture.Window, "StopSearchAsync").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(Field<HashSet<string>>(fixture.Window, "_pendingFolderScans"));
        Assert.Contains(fixture.Root, Field<HashSet<string>>(fixture.Window, "_watchedFolders"));

        // Deliver the same production timer callback deterministically, then allow
        // several monitor polls. A queued checkbox timer must not undo Stop.
        Invoke(fixture.Window, "OnFolderFilterRefreshTimerTick", fixture.Window, EventArgs.Empty);
        await Field<Task>(fixture.Window, "_monitorConfigurationTask").WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(400);
        Assert.True(fixture.Monitor.Activity.IsPaused);
        Assert.False(fixture.Monitor.Activity.IsProcessing);
        Assert.True(Field<bool>(fixture.Window, "_searchStopped"));
        Assert.Equal(0, fixture.FullTraversalCount);
        Assert.Equal(0, await fixture.CountAsync());

        await fixture.ScanAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await fixture.WaitForMonitorIdleAsync();
        Assert.Equal(1, fixture.FullTraversalCount);
        Assert.Equal(1, await fixture.CountAsync());
        Assert.Equal(before, await Fingerprint.ReadAsync(original));
    });

    [Fact]
    public Task SearchingExcludedRootScansOnlyItsExplicitlyIncludedChild() => WpfTestDispatcher.RunAsync(async () =>
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var includedFolder = Path.Combine(fixture.Root, "photos");
        Directory.CreateDirectory(includedFolder);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "excluded-sibling"));
        var included = await fixture.CreatePngAsync(Path.Combine("photos", "included.png"));
        var excluded = await fixture.CreatePngAsync(Path.Combine("excluded-sibling", "excluded.png"));
        var includedBefore = await Fingerprint.ReadAsync(included);
        var excludedBefore = await Fingerprint.ReadAsync(excluded);
        var rules = Field<FolderInclusionRules>(fixture.Window, "_folderInclusion");
        rules.SetIncluded(fixture.Root, false);
        rules.SetIncluded(includedFolder, true);

        // Request the excluded ancestor, as a full search/checkbox action does.
        // Success must await the intersection with the actual included library roots.
        Assert.True(await fixture.ScanAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        await fixture.WaitForMonitorIdleAsync();
        Assert.Equal([includedFolder], fixture.FullTraversalRoots);
        Assert.Equal([includedFolder], fixture.Watchers.ActiveRoots);
        Assert.Equal(0, fixture.FullTraversalCount);
        Assert.Equal(1, fixture.Monitor.Activity.CompletedReconciliationCount);
        Assert.NotNull(await fixture.Store.GetItemAsync(included));
        Assert.Null(await fixture.Store.GetItemAsync(excluded));
        Assert.Equal(1, await fixture.CountAsync());
        Assert.Equal(includedBefore, await Fingerprint.ReadAsync(included));
        Assert.Equal(excludedBefore, await Fingerprint.ReadAsync(excluded));
    });

    [Fact]
    public Task ClickingIncludedFolderUsesOneMonitorTraversalWithoutIndependentDirectScanning() => WpfTestDispatcher.RunAsync(async () =>
    {
        await using var fixture = await SearchFixture.CreateAsync(isolateMonitorScope: true);
        var original = await fixture.CreatePngAsync("included.png");
        var before = await Fingerprint.ReadAsync(original);
        fixture.RegisterLibraryRoot(fixture.Root);
        using var reader = fixture.BlockNextReconciliation(original);
        var browse = fixture.BrowseAsync(fixture.Root);
        await reader.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        // Both user browse and initial monitor discovery refer to this same target.
        // No separate PhotoScanner may write rows while their shared reader is held.
        await Task.Delay(600);
        Assert.False(browse.IsCompleted);
        Assert.True(reader.ReaderOpen);
        Assert.Null(await fixture.Store.GetItemAsync(original));
        Assert.Equal(0, await fixture.CountAsync());
        Assert.Equal([fixture.Root], fixture.ReconciliationPaths);

        reader.Release();
        await browse.WaitAsync(TimeSpan.FromSeconds(15));
        await fixture.WaitForMonitorIdleAsync();
        await fixture.WaitForPublishedFolderAsync(fixture.Root, 1);
        await Task.Delay(300);
        Assert.Equal([fixture.Root], fixture.ReconciliationPaths);
        Assert.Equal(1, await fixture.CountAsync());
        Assert.Equal(before, await Fingerprint.ReadAsync(original));
    });

    [Fact]
    public Task BrowseAfterStopCanBeStoppedAgainAndWaitsForItsActualReaderWithoutResumingLibrary() => WpfTestDispatcher.RunAsync(async () =>
    {
        await using var fixture = await SearchFixture.CreateAsync(isolateMonitorScope: true);
        var original = await fixture.CreatePngAsync("committed-before-stop.png");
        var before = await Fingerprint.ReadAsync(original);
        Assert.True(await fixture.ScanAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        await fixture.WaitForMonitorIdleAsync();
        await fixture.Store.SetFavoriteAsync(original, true);
        var committed = (await fixture.Store.GetItemAsync(original))!;
        await InvokeTask(fixture.Window, "StopSearchAsync").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Monitor.Activity.IsPaused);
        Assert.True(Field<bool>(fixture.Window, "_searchStopped"));

        var later = await fixture.CreatePngAsync("after-stop.png");
        var laterBefore = await Fingerprint.ReadAsync(later);
        using var reader = fixture.BlockNextReconciliation(original);
        var browse = fixture.BrowseAsync(fixture.Root);
        await reader.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var stopButton = (Button)fixture.Window.FindName("CancelScanButton");
        await UntilAsync(() => stopButton.IsEnabled, "Stop was not enabled for the new foreground browse.");
        Assert.True(Field<bool>(fixture.Window, "_searchStopped"));
        Assert.True(fixture.Monitor.Activity.IsPaused);
        Assert.True(reader.ReaderOpen);

        // Exercise the actual routed Stop handler, including its asynchronous drain.
        stopButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, stopButton));
        var stop = Field<Task>(fixture.Window, "_searchStopTask");
        await reader.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(stop.IsCompleted);
        Assert.True(reader.ReaderOpen);
        Assert.True(Field<bool>(fixture.Window, "_searchStopping"));
        Assert.False(stopButton.IsEnabled);
        Assert.Contains("Останавливаю", Assert.IsType<string>(stopButton.Content));
        Assert.Null(await fixture.Store.GetItemAsync(later));
        Invoke(fixture.Window, "QueueLibraryMonitoring");
        await Task.Delay(200);
        Assert.False(stop.IsCompleted);

        reader.Release();
        await stop.WaitAsync(TimeSpan.FromSeconds(15));
        await browse.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(reader.ReaderOpen);
        Assert.True(fixture.Monitor.Activity.IsPaused);
        Assert.False(fixture.Monitor.Activity.IsProcessing);
        Assert.False(stopButton.IsEnabled);
        await AssertCommittedOriginalAsync(fixture.Store, original, committed, before);

        await fixture.BrowseAsync(fixture.Root).WaitAsync(TimeSpan.FromSeconds(15));
        await fixture.WaitForPublishedFolderAsync(fixture.Root, 2);
        Assert.True(fixture.Monitor.Activity.IsPaused);
        Assert.True(Field<bool>(fixture.Window, "_searchStopped"));
        Assert.NotNull(await fixture.Store.GetItemAsync(later));
        await AssertCommittedOriginalAsync(fixture.Store, original, committed, before);
        Assert.Equal(laterBefore, await Fingerprint.ReadAsync(later));
    });

    [Fact]
    public Task ChangingBrowseFromAToBPublishesBWhileCancelledAReaderDrainsAndPreservesCommittedRows() => WpfTestDispatcher.RunAsync(async () =>
    {
        await using var fixture = await SearchFixture.CreateAsync(isolateMonitorScope: true);
        var pathA = await fixture.CreatePngAsync(Path.Combine("A", "favorite.png"));
        var pathB = await fixture.CreatePngAsync(Path.Combine("B", "latest.png"));
        var folderA = Path.GetDirectoryName(pathA)!;
        var folderB = Path.GetDirectoryName(pathB)!;
        var beforeA = await Fingerprint.ReadAsync(pathA);
        var beforeB = await Fingerprint.ReadAsync(pathB);
        var savedA = LibraryCatalogSynchronizer.Available(new FileSystemObservationProbe().ProbeFile(pathA));
        savedA.IsFavorite = true;
        await fixture.Store.UpsertItemsAsync([savedA]);
        var committedA = (await fixture.Store.GetItemAsync(pathA))!;
        using var reader = fixture.BlockNextReconciliation(pathA, folderA);
        var browseA = fixture.BrowseAsync(folderA);
        await reader.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.WaitForPublishedFolderAsync(folderA, 1);

        var publishedAfterB = new List<string?>();
        fixture.Window.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindow.PhotoRows) && fixture.Window.PhotoRows is VirtualPhotoRows)
                publishedAfterB.Add(Field<CatalogViewQuery?>(fixture.Window, "_currentQuery")?.Folder);
        };
        var browseB = fixture.BrowseAsync(folderB);
        Assert.Equal(folderB, Field<string>(fixture.Window, "_activeFolder"));
        Assert.Equal(folderB, ((TextBlock)fixture.Window.FindName("ViewTitleText")).Text);
        // The UI can project B's existing (empty) catalog before A's actual reader exits.
        await fixture.WaitForPublishedFolderAsync(folderB, 0);
        await reader.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(reader.ReaderOpen);
        Assert.False(browseB.IsCompleted);
        reader.Release();
        await browseA.WaitAsync(TimeSpan.FromSeconds(15));
        await browseB.WaitAsync(TimeSpan.FromSeconds(15));
        await fixture.WaitForPublishedFolderAsync(folderB, 1);
        Assert.NotEmpty(publishedAfterB);
        Assert.All(publishedAfterB, folder => Assert.Equal(folderB, folder));
        var rows = Assert.IsType<VirtualPhotoRows>(fixture.Window.PhotoRows);
        Assert.Contains(rows.LoadedItems, item => item.Path == pathB);
        Assert.DoesNotContain(rows.LoadedItems, item => item.Path == pathA);
        Assert.Empty(Field<HashSet<string>>(fixture.Window, "_watchedFolders"));
        await AssertCommittedOriginalAsync(fixture.Store, pathA, committedA, beforeA);
        Assert.Equal(beforeB, await Fingerprint.ReadAsync(pathB));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BrowsingExcludedFolderHonorsRecursionWithoutExpandingLibrary(bool recursive) => WpfTestDispatcher.RunAsync(async () =>
    {
        await using var fixture = await SearchFixture.CreateAsync(isolateMonitorScope: true);
        var direct = await fixture.CreatePngAsync(Path.Combine("excluded", "direct.png"));
        var nested = await fixture.CreatePngAsync(Path.Combine("excluded", "nested", "nested.png"));
        var sibling = await fixture.CreatePngAsync(Path.Combine("sibling", "unrelated.png"));
        var folder = Path.GetDirectoryName(direct)!;
        var fingerprints = new Dictionary<string, Fingerprint>();
        foreach (var path in new[] { direct, nested, sibling }) fingerprints[path] = await Fingerprint.ReadAsync(path);
        var rules = Field<FolderInclusionRules>(fixture.Window, "_folderInclusion");
        rules.SetIncluded(fixture.Root, false);
        var excludedBefore = rules.ExcludedFolders.ToArray();
        var includedBefore = rules.IncludedFolders.ToArray();
        SetField(fixture.Window, "_includeSubfolders", recursive);

        await fixture.BrowseAsync(folder).WaitAsync(TimeSpan.FromSeconds(15));
        await fixture.WaitForMonitorIdleAsync();
        await fixture.WaitForPublishedFolderAsync(folder, recursive ? 2 : 1);
        Assert.Equal([folder], fixture.ReconciliationPaths);
        Assert.Equal([folder], fixture.Watchers.ActiveRoots);
        Assert.Equal(recursive, fixture.Watchers.IsRecursive(folder));
        Assert.Empty(Field<HashSet<string>>(fixture.Window, "_watchedFolders"));
        Assert.Equal(excludedBefore, rules.ExcludedFolders);
        Assert.Equal(includedBefore, rules.IncludedFolders);
        Assert.NotNull(await fixture.Store.GetItemAsync(direct));
        if (recursive) Assert.NotNull(await fixture.Store.GetItemAsync(nested));
        else Assert.Null(await fixture.Store.GetItemAsync(nested));
        Assert.Null(await fixture.Store.GetItemAsync(sibling));
        Assert.Equal(recursive ? 2 : 1, await fixture.CountAsync());
        foreach (var pair in fingerprints) Assert.Equal(pair.Value, await Fingerprint.ReadAsync(pair.Key));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BrowsingChildOfIdleWatchedParentUsesOneScopedPassAndExistingParentWatcher(bool recursive) => WpfTestDispatcher.RunAsync(async () =>
    {
        await using var fixture = await SearchFixture.CreateAsync(isolateMonitorScope: true);
        var original = await fixture.CreatePngAsync("already-catalogued.png");
        Assert.True(await fixture.ScanAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        await fixture.WaitForMonitorIdleAsync();
        await fixture.Store.SetFavoriteAsync(original, true);
        var committed = (await fixture.Store.GetItemAsync(original))!;
        Assert.Equal([fixture.Root], fixture.Watchers.ActiveRoots);
        Assert.True(fixture.Watchers.IsRecursive(fixture.Root));
        var previousBatches = fixture.Batches.Length;
        var previousParentTraversals = fixture.FullTraversalCount;

        // Create these only AFTER the parent is idle. The controlled watcher emits
        // no events, so neither metadata nor the initial pass can discover them.
        var direct = await fixture.CreatePngAsync(Path.Combine("photos", "direct.png"));
        var nested = await fixture.CreatePngAsync(Path.Combine("photos", "nested", "nested.png"));
        var unrelated = await fixture.CreatePngAsync(Path.Combine("other", "unrelated.png"));
        var child = Path.GetDirectoryName(direct)!;
        var fingerprints = new Dictionary<string, Fingerprint>();
        foreach (var path in new[] { original, direct, nested, unrelated }) fingerprints[path] = await Fingerprint.ReadAsync(path);
        SetField(fixture.Window, "_includeSubfolders", recursive);
        using var reader = fixture.BlockNextReconciliation(direct, child);
        var browse = fixture.BrowseAsync(child);
        await reader.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        Assert.False(browse.IsCompleted);
        Assert.Null(await fixture.Store.GetItemAsync(direct));
        Assert.Null(await fixture.Store.GetItemAsync(nested));
        Assert.Null(await fixture.Store.GetItemAsync(unrelated));

        reader.Release();
        await browse.WaitAsync(TimeSpan.FromSeconds(15));
        await fixture.WaitForMonitorIdleAsync();
        await fixture.WaitForPublishedFolderAsync(child, recursive ? 2 : 1);
        await Task.Delay(300);
        var work = Assert.Single(fixture.Batches.Skip(previousBatches), batch => batch.BrowseTarget is not null ||
            batch.ReconcileRoots.Count > 0 || batch.ChangedPaths.Count > 0 || (batch.DirectoryChanges?.Count ?? 0) > 0);
        Assert.NotNull(work.BrowseTarget);
        Assert.Equal(child, work.BrowseTarget.Path);
        Assert.Equal(recursive, work.BrowseTarget.IncludeSubdirectories);
        Assert.Empty(work.ReconcileRoots);
        Assert.Empty(work.ChangedPaths);
        Assert.Empty(work.DirectoryChanges ?? []);
        Assert.Equal(previousParentTraversals, fixture.FullTraversalCount);
        Assert.Equal([fixture.Root], fixture.Watchers.CreatedRoots);
        Assert.Equal([fixture.Root], fixture.Watchers.ActiveRoots);
        Assert.True(fixture.Watchers.IsRecursive(fixture.Root));
        Assert.Equal([fixture.Root], Field<HashSet<string>>(fixture.Window, "_watchedFolders"));
        Assert.NotNull(await fixture.Store.GetItemAsync(direct));
        if (recursive) Assert.NotNull(await fixture.Store.GetItemAsync(nested));
        else Assert.Null(await fixture.Store.GetItemAsync(nested));
        Assert.Null(await fixture.Store.GetItemAsync(unrelated));
        Assert.Equal(recursive ? 3 : 2, await fixture.CountAsync());
        await AssertCommittedOriginalAsync(fixture.Store, original, committed, fingerprints[original]);
        foreach (var pair in fingerprints) Assert.Equal(pair.Value, await Fingerprint.ReadAsync(pair.Key));
    });

    private static async Task AssertCommittedOriginalAsync(SqliteDesktopCatalogStore store, string path,
        SavedMediaItem before, Fingerprint fingerprint)
    {
        var after = (await store.GetItemAsync(path))!;
        Assert.Equal(before.AssetId, after.AssetId);
        Assert.True(after.IsFavorite);
        Assert.Equal(before.SizeBytes, after.SizeBytes);
        Assert.Equal(before.FileModifiedAt, after.FileModifiedAt);
        Assert.Equal(FileAvailability.Available, after.Availability);
        Assert.Equal(fingerprint, await Fingerprint.ReadAsync(path));
    }

    private sealed class SearchFixture : IAsyncDisposable
    {
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
        private readonly ConcurrentQueue<LibraryMonitorBatch> _batches = new();
        private sealed record PendingReader(string Target, ReaderBarrier Reader);
        private PendingReader? _nextReader;
        private readonly List<ReaderBarrier> _readers = [];
        public string Root { get; } = Directory.CreateTempSubdirectory("photoshelf-mainwindow-search-tests-").FullName;
        public MainWindow Window { get; }
        public SqliteDesktopCatalogStore Store { get; }
        public ControlledWatchers Watchers { get; } = new();
        public LibraryChangeCoordinator Monitor => Field<LibraryChangeCoordinator>(Window, "_libraryMonitor");
        public LibraryMonitorBatch[] Batches => _batches.ToArray();
        public string[] FullTraversalRoots => _batches.SelectMany(batch => batch.ReconcileRoots).ToArray();
        public string[] ReconciliationPaths => _batches.SelectMany(batch => batch.ReconcileRoots
            .Concat((batch.DirectoryChanges ?? []).Select(change => change.Path))
            .Concat(batch.BrowseTarget is { } browse ? new[] { browse.Path } : Array.Empty<string>())).ToArray();
        public int FullTraversalCount => _batches.Sum(batch => batch.ReconcileRoots.Count(path =>
            path.Equals(Root, StringComparison.OrdinalIgnoreCase)));

        private SearchFixture()
        {
            Assert.True(LocalCatalogStore.IsIsolatedSmokeCatalog);
            Assert.Equal(IsolatedTestCatalog.DirectoryPath, LocalCatalogStore.CatalogDirectory);
            Window = new MainWindow(new LocalCatalogState { IncludeSystemFolders = true, ReadItemsFromSqlite = true });
            Store = Field<SqliteDesktopCatalogStore>(Window, "_desktopCatalogStore");
            Func<Func<LibraryMonitorBatch, CancellationToken, Task>, LibraryChangeCoordinator> factory = callback => new(
                async (batch, token) =>
                {
                    _batches.Enqueue(batch);
                    var reader = Volatile.Read(ref _nextReader);
                    if (reader is not null &&
                        (batch.ReconcileRoots.Contains(reader.Target, StringComparer.OrdinalIgnoreCase) ||
                         (batch.DirectoryChanges ?? []).Any(change => change.Path.Equals(reader.Target, StringComparison.OrdinalIgnoreCase)) ||
                         batch.BrowseTarget?.Path.Equals(reader.Target, StringComparison.OrdinalIgnoreCase) == true) &&
                        ReferenceEquals(Interlocked.CompareExchange(ref _nextReader, null, reader), reader))
                        await reader.Reader.HoldAsync(token);
                    await callback(batch, token);
                }, new LibraryMonitorOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(20), Debounce = TimeSpan.Zero,
                    RootProbeInterval = TimeSpan.FromHours(1), ReconciliationInterval = TimeSpan.FromHours(1)
                }, Watchers);
            Property(Window, "LibraryMonitorFactory").SetValue(Window, factory);
        }

        public static async Task<SearchFixture> CreateAsync(bool isolateMonitorScope = false)
        {
            var fixture = new SearchFixture();
            try
            {
                fixture.Window.Show();
                var ready = (Task<Exception?>)Property(fixture.Window, "InitialCatalogReady").GetValue(fixture.Window)!;
                Assert.Null(await ready.WaitAsync(TimeSpan.FromSeconds(15)));
                await Field<Task>(fixture.Window, "_metadataTask").WaitAsync(TimeSpan.FromSeconds(10));
                // The process-isolated catalog is shared by serialized tests. Browse
                // scenarios choose their own roots, never adopt previous fixture rows.
                if (isolateMonitorScope) SetField(fixture.Window, "_monitorRootsRestored", true);
                // Startup cannot watch adopted folders from another test. Enable the
                // isolated production monitor only for our explicit synthetic search.
                Property(fixture.Window, "AllowIsolatedLibraryMonitoring").SetValue(fixture.Window, true);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async Task<string> CreatePngAsync(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, Png);
            return path;
        }

        public ReaderBarrier BlockNextReconciliation(string path, string? target = null)
        {
            var reader = new ReaderBarrier(path);
            Assert.Null(Interlocked.CompareExchange(ref _nextReader, new PendingReader(target ?? Root, reader), null));
            _readers.Add(reader);
            return reader;
        }

        public Task<bool> ScanAsync() => (Task<bool>)InvokeTask(Window, "ScanRootsAsync", new[] { Root }, false);

        public void RegisterLibraryRoot(string path) => Invoke(Window, "RegisterWatchedFolder", path);

        public Task BrowseAsync(string path)
        {
            var node = new FolderNode(path);
            node.ApplyInclusion(Field<FolderInclusionRules>(Window, "_folderInclusion"));
            Invoke(Window, "OnFolderTreeSelectedItemChanged", Window.FindName("FolderTree"),
                new RoutedPropertyChangedEventArgs<object>(null!, node, TreeView.SelectedItemChangedEvent));
            return Field<Task>(Window, "_browseTask");
        }

        public async Task WaitForPublishedFolderAsync(string folder, long count)
        {
            await UntilAsync(() => !Field<bool>(Window, "_isProjecting") && !Field<bool>(Window, "_projectionQueued") &&
                Field<CatalogViewQuery?>(Window, "_currentQuery")?.Folder == folder &&
                Window.PhotoRows is VirtualPhotoRows rows && rows.ItemCount == count,
                "The selected folder was not published with its expected catalog rows.");
        }

        public Task<long> CountAsync() => Store.CountAsync(new CatalogViewQuery
            { Folder = Root, ViewMode = "Folder", IncludeSubfolders = true, IncludeSystemFolders = true });

        public async Task WaitForMonitorIdleAsync()
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                var state = Monitor.Activity;
                if (!state.IsProcessing && state.PendingPathCount == 0 && state.PendingDirectoryCount == 0 &&
                    state.PendingReconciliationRootCount == 0) return;
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Monitor did not become idle.");
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var reader in _readers) reader.Release();
            // Unloaded cancels previews but is not proof that a synchronous codec
            // has closed its file. Drain those real readers before fixture cleanup.
            using var previews = await AsyncMediaImage.PauseForFileOperationsAsync(
                Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase));
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Window.Closed += (_, _) => closed.TrySetResult();
            Window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(Field<bool>(Window, "_closeReady"));
            foreach (var reader in _readers) Assert.False(reader.ReaderOpen);
            // Only synthetic originals are removed, after preview drain and production
            // shutdown of monitor/scan/browse/metadata readers and catalog writers.
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ReaderBarrier(string path) : IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readerOpen;
        public Task Entered => _entered.Task;
        public Task CancellationObserved => _cancelled.Task;
        public bool ReaderOpen => Volatile.Read(ref _readerOpen) != 0;

        public async Task HoldAsync(CancellationToken token)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Assert.NotEqual(-1, stream.ReadByte());
                Volatile.Write(ref _readerOpen, 1);
                using var registration = token.Register(() => _cancelled.TrySetResult());
                _entered.TrySetResult();
                // Deliberately noncooperative read lease: cancellation is observable,
                // but the file remains open until the underlying reader returns.
                await _release.Task;
            }
            finally { Volatile.Write(ref _readerOpen, 0); }
            token.ThrowIfCancellationRequested();
        }

        public void Release() => _release.TrySetResult();
        public void Dispose() => Release();
    }

    private sealed class ControlledWatchers : ILibraryWatcherFactory
    {
        private readonly ConcurrentDictionary<string, Watcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
        public string[] CreatedRoots => _watchers.Keys.ToArray();
        public string[] ActiveRoots => _watchers.Where(pair => !pair.Value.IsDisposed).Select(pair => pair.Key).ToArray();
        public IDisposable Watch(string root, bool includeSubdirectories, Action<LibraryWatchEvent> onChange, Action<Exception> onError)
        {
            var watcher = new Watcher(onChange, includeSubdirectories);
            _watchers.AddOrUpdate(root, watcher, (_, previous) =>
            {
                Assert.True(previous.IsDisposed, "Duplicate active watcher for the same root.");
                return watcher;
            });
            return watcher;
        }
        public bool IsRecursive(string root) => _watchers[root].Recursive;
        public void Emit(string path)
        {
            var watcher = Assert.Single(_watchers.Values, item => !item.IsDisposed);
            watcher.OnChange(new(path));
        }
        private sealed class Watcher(Action<LibraryWatchEvent> onChange, bool recursive) : IDisposable
        {
            public Action<LibraryWatchEvent> OnChange { get; } = onChange;
            public bool Recursive { get; } = recursive;
            public bool IsDisposed { get; private set; }
            public void Dispose() => IsDisposed = true;
        }
    }

    private sealed record Fingerprint(string Hash, long Length, DateTime LastWriteTimeUtc)
    {
        public static async Task<Fingerprint> ReadAsync(string path) => new(
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))),
            new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));
    }

    private static async Task UntilAsync(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), message);
            await Task.Delay(20);
        }
    }

    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static PropertyInfo Property(object instance, string name) =>
        instance.GetType().GetProperty(name, InstanceMembers) ?? throw new MissingMemberException(instance.GetType().Name, name);
    private static FieldInfo FieldInfo(object instance, string name) =>
        instance.GetType().GetField(name, InstanceMembers) ?? throw new MissingFieldException(instance.GetType().Name, name);
    private static T Field<T>(object instance, string name) => (T)FieldInfo(instance, name).GetValue(instance)!;
    private static void SetField(object instance, string name, object value) => FieldInfo(instance, name).SetValue(instance, value);
    private static object? Invoke(object instance, string name, params object[] args) =>
        (instance.GetType().GetMethod(name, InstanceMembers) ?? throw new MissingMethodException(instance.GetType().Name, name)).Invoke(instance, args);
    private static Task InvokeTask(object instance, string name, params object[] args) => (Task)Invoke(instance, name, args)!;
}
