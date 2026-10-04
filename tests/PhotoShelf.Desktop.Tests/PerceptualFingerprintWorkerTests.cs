using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Duplicates;
using PhotoShelf.Infrastructure.Sqlite;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

public sealed class PerceptualFingerprintWorkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RequestsDuringPassIndexNewOrChangedFileBehindCursor(bool changeExisting) => WpfTestDispatcher.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        if (changeExisting)
        {
            await fixture.WritePngAsync("a.png");
            _ = fixture.Start();
            await fixture.WaitForIdleAsync();
            await fixture.AssertIndexedAsync("a.png");
        }
        var last = await fixture.WritePngAsync("z.png");
        using var reader = fixture.BlockReader(last);
        var first = fixture.Start();
        await reader.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var earlier = await fixture.WritePngAsync("a.png", 190);
        var before = await Snapshot.ReadAsync(earlier);
        var lastBefore = await Snapshot.ReadAsync(last);

        for (var i = 0; i < 20; i++) Assert.Same(first, fixture.Start());
        Assert.True(fixture.RefreshPending);
        Assert.Equal(1, fixture.MaxReaders);
        reader.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.WaitForIdleAsync();

        Assert.NotSame(first, fixture.Worker);
        Assert.False(fixture.RefreshPending);
        await fixture.AssertIndexedAsync("a.png", "z.png");
        Assert.Equal(changeExisting ? 2 : 1, fixture.ReadCount(earlier));
        Assert.Equal(1, fixture.ReadCount(last));
        Assert.Equal(1, fixture.MaxReaders);
        Assert.Equal(before, await Snapshot.ReadAsync(earlier));
        Assert.Equal(lastBefore, await Snapshot.ReadAsync(last));

        // A fresh request uses the cache. Neither this request nor an old queued
        // completion should create an endless sequence of empty passes.
        var cachedPass = fixture.Start();
        await fixture.WaitForIdleAsync();
        Assert.Same(cachedPass, fixture.Worker);
        Assert.Equal(changeExisting ? 2 : 1, fixture.ReadCount(earlier));
        Assert.Equal(1, fixture.ReadCount(last));
    });

    [Theory]
    [InlineData("_backgroundProcessingPaused")]
    [InlineData("_fileOperationActive")]
    [InlineData("_hasPendingRecovery")]
    [InlineData("_closing")]
    public Task QueuedRefreshRechecksGuardsAndExplicitRestartConsumesPendingRequest(string guard) => WpfTestDispatcher.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        var path = await fixture.WritePngAsync("z.png");
        using var reader = fixture.BlockReader(path);
        var first = fixture.Start();
        await reader.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.WritePngAsync("a.png");
        _ = fixture.Start();
        // Set the guard synchronously at task completion, before returning to the
        // dispatcher. Thread-pool scheduling of the test's await cannot race the post.
        var guarded = first.ContinueWith(_ =>
        {
            Assert.True(fixture.Window.Dispatcher.CheckAccess());
            Set(fixture.Window, guard, true);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        reader.Release();
        await guarded.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await FlushDispatcherAsync();
            Assert.Same(first, fixture.Worker);
            Assert.True(fixture.RefreshPending);
            Assert.Equal(0, fixture.ReadCount(Path.Combine(fixture.Root, "a.png")));
        }
        finally { Set(fixture.Window, guard, false); }

        var restarted = fixture.Start();
        Assert.False(fixture.RefreshPending);
        await fixture.WaitForIdleAsync();
        Assert.Same(restarted, fixture.Worker);
        await fixture.AssertIndexedAsync("a.png", "z.png");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PauseOrFileOperationWaitsForActualReaderAndPreventsPendingRestart(bool fileOperation) => WpfTestDispatcher.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        var path = await fixture.WritePngAsync("z.png");
        var before = await Snapshot.ReadAsync(path);
        using var reader = fixture.BlockReader(path);
        var first = fixture.Start();
        await reader.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        _ = fixture.Start();

        Task stop;
        if (fileOperation)
        {
            // These are the production guard and drain used before moves/quarantine.
            Set(fixture.Window, "_fileOperationActive", true);
            stop = InvokeTask(fixture.Window, "StopCatalogWritersAsync", true);
        }
        else stop = InvokeTask(fixture.Window, "ToggleBackgroundProcessingAsync");

        await reader.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(stop.IsCompleted);
        Assert.True(reader.IsOpen);
        Assert.Throws<IOException>(() =>
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        });
        reader.Release();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        await FlushDispatcherAsync();
        Assert.False(reader.IsOpen);
        Assert.True(first.IsCompleted);
        Assert.Same(first, fixture.Worker);
        Assert.True(fixture.RefreshPending);
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await FlushDispatcherAsync();
            Assert.Same(first, fixture.Worker);
        }
        Assert.Equal(before, await Snapshot.ReadAsync(path));

        if (fileOperation)
        {
            Set(fixture.Window, "_fileOperationActive", false);
            _ = fixture.Start();
        }
        else await InvokeTask(fixture.Window, "ToggleBackgroundProcessingAsync");
        await fixture.WaitForIdleAsync();
        await fixture.AssertIndexedAsync("z.png");
        Assert.Equal(before, await Snapshot.ReadAsync(path));
        Assert.Equal(1, fixture.MaxReaders);
    });

    [Fact]
    public Task ClosingWaitsForReaderWithoutReplayingPendingRefresh() => WpfTestDispatcher.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        var path = await fixture.WritePngAsync("z.png");
        var before = await Snapshot.ReadAsync(path);
        using var reader = fixture.BlockReader(path);
        var first = fixture.Start();
        await reader.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        _ = fixture.Start();
        var closed = fixture.CloseAsync();
        await reader.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(closed.IsCompleted);
        Assert.True(reader.IsOpen);
        reader.Release();
        await closed.WaitAsync(TimeSpan.FromSeconds(15));
        await FlushDispatcherAsync();
        Assert.False(reader.IsOpen);
        Assert.True(first.IsCompleted);
        Assert.Same(first, fixture.Worker);
        Assert.Equal(1, fixture.ReadCount(path));
        Assert.Equal(before, await Snapshot.ReadAsync(path));
    });

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("photoshelf-fingerprint-worker-").FullName;
        public MainWindow Window { get; }
        public SqliteDesktopCatalogStore Catalog { get; }
        private readonly PerceptualFingerprintStore _fingerprints;
        private readonly ConcurrentDictionary<string, int> _reads = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ReaderBarrier> _readers = [];
        private ReaderBarrier? _nextReader;
        private int _activeReaders;
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaxReaders;
        public Task Worker => Get<Task>(Window, "_fingerprintTask");
        public bool RefreshPending => Get<bool>(Window, "_fingerprintRefreshPending");

        private Fixture()
        {
            Assert.True(LocalCatalogStore.IsIsolatedSmokeCatalog);
            Assert.Equal(IsolatedTestCatalog.DirectoryPath, LocalCatalogStore.CatalogDirectory);
            Window = new MainWindow(new LocalCatalogState
            {
                ReadItemsFromSqlite = true, IncludeSystemFolders = true,
                BackgroundProcessingPaused = true, ViewMode = "Favorites"
            });
            Window.Closed += (_, _) => _closed.TrySetResult();
            Catalog = Get<SqliteDesktopCatalogStore>(Window, "_desktopCatalogStore");
            _fingerprints = Get<PerceptualFingerprintStore>(Window, "_perceptualFingerprintStore");
            typeof(MainWindow).GetProperty("FingerprintReader", Members)!.SetValue(Window,
                (Func<string, CancellationToken, PerceptualFingerprint>)Read);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try
            {
                fixture.Window.Show();
                var ready = (Task<Exception?>)typeof(MainWindow).GetProperty("InitialCatalogReady", Members)!.GetValue(fixture.Window)!;
                Assert.Null(await ready.WaitAsync(TimeSpan.FromSeconds(15)));
                Set(fixture.Window, "_backgroundProcessingPaused", false);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private PerceptualFingerprint Read(string path, CancellationToken token)
        {
            if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return MediaBitmapLoader.LoadPerceptualFingerprint(path, token);
            _reads.AddOrUpdate(path, 1, (_, count) => count + 1);
            var active = Interlocked.Increment(ref _activeReaders);
            int peak;
            do { peak = Volatile.Read(ref MaxReaders); }
            while (active > peak && Interlocked.CompareExchange(ref MaxReaders, active, peak) != peak);
            try
            {
                var reader = Volatile.Read(ref _nextReader);
                if (reader?.Path == path && ReferenceEquals(Interlocked.CompareExchange(ref _nextReader, null, reader), reader))
                    reader.Hold(token);
                return MediaBitmapLoader.LoadPerceptualFingerprint(path, token);
            }
            finally { Interlocked.Decrement(ref _activeReaders); }
        }

        public Task Start() { Invoke(Window, "StartPerceptualFingerprintIndexing"); return Worker; }
        public Task CloseAsync() { Window.Close(); return _closed.Task; }
        public int ReadCount(string path) => _reads.GetValueOrDefault(path);
        public ReaderBarrier BlockReader(string path)
        {
            var reader = new ReaderBarrier(path);
            Assert.Null(Interlocked.CompareExchange(ref _nextReader, reader, null));
            _readers.Add(reader);
            return reader;
        }

        public async Task<string> WritePngAsync(string name, byte value = 40)
        {
            var path = Path.Combine(Root, name);
            var modified = File.Exists(path) ? File.GetLastWriteTimeUtc(path).AddSeconds(5) : DateTime.UtcNow;
            var pixels = Enumerable.Repeat(value, 32 * 32).ToArray();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 32, 96, 96, PixelFormats.Gray8, null, pixels, 32)));
            using var bytes = new MemoryStream();
            encoder.Save(bytes);
            await File.WriteAllBytesAsync(path, bytes.ToArray());
            File.SetLastWriteTimeUtc(path, modified);
            var file = new FileInfo(path);
            await Catalog.UpsertItemsAsync([new SavedMediaItem
            {
                Path = path, SizeBytes = file.Length, FileModifiedAt = file.LastWriteTime,
                Availability = FileAvailability.Available
            }]);
            return path;
        }

        public async Task AssertIndexedAsync(params string[] names)
        {
            var items = new List<SavedMediaItem>();
            foreach (var name in names) items.Add((await Catalog.GetItemAsync(Path.Combine(Root, name)))!);
            var indexed = await _fingerprints.ReadObservedBatchAsync(items);
            Assert.Equal(names.Length, indexed.Count);
            Assert.Empty(await _fingerprints.QueryDuePageAsync(new([Root], [], true, DateTime.UtcNow)));
        }

        public async Task WaitForIdleAsync()
        {
            var watch = Stopwatch.StartNew();
            do
            {
                await Worker.WaitAsync(TimeSpan.FromSeconds(10));
                await FlushDispatcherAsync();
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Fingerprint passes did not settle.");
            } while (!Worker.IsCompleted);
            Assert.False(RefreshPending);
            var settled = Worker;
            await Task.Delay(200);
            await FlushDispatcherAsync();
            Assert.Same(settled, Worker);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var reader in _readers) reader.Release();
            Set(Window, "_fileOperationActive", false);
            if (!_closed.Task.IsCompleted) await CloseAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(Get<bool>(Window, "_closeReady"));
            foreach (var reader in _readers) Assert.False(reader.IsOpen);
            // Only this fixture's PNGs, after production shutdown/drain. The shared
            // isolated catalog is retained; no user library can be reached.
            Directory.Delete(Root, true);
        }
    }

    private sealed class ReaderBarrier(string path) : IDisposable
    {
        public string Path { get; } = path;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(false);
        private int _open;
        public Task Entered => _entered.Task;
        public Task CancellationObserved => _cancelled.Task;
        public bool IsOpen => Volatile.Read(ref _open) != 0;
        public void Hold(CancellationToken token)
        {
            try
            {
                using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Assert.NotEqual(-1, stream.ReadByte());
                Volatile.Write(ref _open, 1);
                using var registration = token.Register(() => _cancelled.TrySetResult());
                _entered.TrySetResult();
                // Cancellation is observable but cannot pretend that a synchronous
                // codec has returned and released its actual Windows file handle.
                if (!_release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Test reader was not released.");
            }
            finally { Volatile.Write(ref _open, 0); }
            token.ThrowIfCancellationRequested();
        }
        public void Release() => _release.Set();
        public void Dispose() => Release();
    }

    private sealed record Snapshot(string Hash, long Size, DateTime Modified)
    {
        public static async Task<Snapshot> ReadAsync(string path) => new(
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))),
            new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));
    }
    private static Task FlushDispatcherAsync() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Members)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Members)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, Members)!.Invoke(window, args);
    private static Task InvokeTask(MainWindow window, string name, params object[] args) => (Task)Invoke(window, name, args)!;
}
