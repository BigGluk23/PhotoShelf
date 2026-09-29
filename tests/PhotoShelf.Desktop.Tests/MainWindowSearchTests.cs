using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
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

        Invoke(fixture.Window, "OnFolderIncludedChanged", checkbox, new RoutedEventArgs());
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
        private ReaderBarrier? _nextReader;
        private readonly List<ReaderBarrier> _readers = [];
        public string Root { get; } = Directory.CreateTempSubdirectory("photoshelf-mainwindow-search-tests-").FullName;
        public MainWindow Window { get; }
        public SqliteDesktopCatalogStore Store { get; }
        public ControlledWatchers Watchers { get; } = new();
        public LibraryChangeCoordinator Monitor => Field<LibraryChangeCoordinator>(Window, "_libraryMonitor");
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
                    if (batch.ReconcileRoots.Contains(Root, StringComparer.OrdinalIgnoreCase) &&
                        Interlocked.Exchange(ref _nextReader, null) is { } reader)
                        await reader.HoldAsync(token);
                    await callback(batch, token);
                }, new LibraryMonitorOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(20), Debounce = TimeSpan.Zero,
                    RootProbeInterval = TimeSpan.FromHours(1), ReconciliationInterval = TimeSpan.FromHours(1)
                }, Watchers);
            Property(Window, "LibraryMonitorFactory").SetValue(Window, factory);
        }

        public static async Task<SearchFixture> CreateAsync()
        {
            var fixture = new SearchFixture();
            try
            {
                fixture.Window.Show();
                var ready = (Task<Exception?>)Property(fixture.Window, "InitialCatalogReady").GetValue(fixture.Window)!;
                Assert.Null(await ready.WaitAsync(TimeSpan.FromSeconds(15)));
                await Field<Task>(fixture.Window, "_metadataTask").WaitAsync(TimeSpan.FromSeconds(10));
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
            await File.WriteAllBytesAsync(path, Png);
            return path;
        }

        public ReaderBarrier BlockNextReconciliation(string path)
        {
            var reader = new ReaderBarrier(path);
            Assert.Null(Interlocked.CompareExchange(ref _nextReader, reader, null));
            _readers.Add(reader);
            return reader;
        }

        public Task ScanAsync() => InvokeTask(Window, "ScanRootsAsync", new[] { Root }, false);

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
                Directory.EnumerateFiles(Root).ToHashSet(StringComparer.OrdinalIgnoreCase));
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
        public IDisposable Watch(string root, bool includeSubdirectories, Action<LibraryWatchEvent> onChange, Action<Exception> onError)
        {
            var watcher = new Watcher(onChange);
            Assert.True(_watchers.TryAdd(root, watcher), "Duplicate watcher for the same root.");
            return watcher;
        }
        public void Emit(string path)
        {
            var watcher = Assert.Single(_watchers.Values, item => !item.IsDisposed);
            watcher.OnChange(new(path));
        }
        private sealed class Watcher(Action<LibraryWatchEvent> onChange) : IDisposable
        {
            public Action<LibraryWatchEvent> OnChange { get; } = onChange;
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
