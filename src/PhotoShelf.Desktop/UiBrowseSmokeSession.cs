using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoShelf.Application.Catalog;

namespace PhotoShelf.Desktop;

/// <summary>Actual production browse/monitor/metadata interactions on owned synthetic files.</summary>
internal sealed class UiBrowseSmokeSession
{
    private const int NoiseDirectoriesPerPhase = 1100;
    private readonly Dictionary<string, byte[]> _originalHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<object> _stability = [];
    private readonly Stopwatch _elapsed = new();
    private string _phase = "not-started";
    private string _mediaRoot = "", _parent = "", _pictures = "", _proof = "", _png = "", _video = "", _freshFolder = "", _freshPng = "";
    private int _rowPublications, _emptyTransitions, _wrongFolderPublications;
    private string? _expectedPublicationFolder;
    private bool _closed, _closeRequested, _readersDrained, _writersDrained;
    private IDisposable? _mediaPause;
    public bool ChecksPassed { get; private set; }
    public bool GracefulExit => _closed && _closeRequested && _readersDrained && _writersDrained;
    private bool _monitorObserved, _checkboxVerified, _directVerified, _recursiveVerified, _rapidVerified, _hashesVerified, _freshDiscoveryVerified;
    private double _firstPreviewMilliseconds;
    private double _firstDiscoveryMilliseconds;

    public async Task SeedCatalogAsync()
    {
        _phase = "seed-owned-fixtures";
        Require(LocalCatalogStore.IsIsolatedSmokeCatalog, "Browse smoke requires an isolated catalog.");
        _mediaRoot = Directory.CreateTempSubdirectory("PhotoShelf-browse-smoke-").FullName;
        _parent = Path.Combine(_mediaRoot, "Users", "Alex");
        _pictures = Path.Combine(_parent, "Pictures");
        _proof = Path.Combine(_mediaRoot, "MonitorProof");
        _freshFolder = Path.Combine(_mediaRoot, "NeverIndexed");
        _freshPng = Path.Combine(_freshFolder, "new-direct.png");
        _png = Path.Combine(_pictures, "direct.png");
        _video = Path.Combine(_pictures, "Videos", "synthetic.mp4");
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_video)!); Directory.CreateDirectory(_proof);
            Directory.CreateDirectory(_freshFolder);
            WritePng(_png);
            WritePng(_freshPng);
            // Only extension/type/count are under test. No video decoder success is claimed.
            File.WriteAllBytes(_video, [0, 0, 0, 16, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0]);
            foreach (var path in new[] { _png, _video, _freshPng }) _originalHashes[path] = SHA256.HashData(File.ReadAllBytes(path));
        });
        var store = new SqliteDesktopCatalogStore();
        await store.InitializeAsync();
        await store.SaveAsync(new LocalCatalogState
        {
            IncludeSystemFolders = true, ShowVideos = true, IncludeSubfolders = false,
            ViewMode = "Folder", ActiveFolder = _parent, DateGroupingMode = "FileDate",
            ExcludedFolders = [_mediaRoot], IncludedFolders = [_pictures, _proof], WatchedFolders = [_mediaRoot]
        }, saveItems: false);
        await store.UpsertItemsAsync(new[] { _png, _video }.Select(path => new SavedMediaItem
        {
            Path = path, SizeBytes = new FileInfo(path).Length, FileModifiedAt = File.GetLastWriteTime(path),
            IsVideo = PhotoItem.IsVideoPath(path), MetadataIndexed = false
        }));
    }

    public void PrepareWindow(MainWindow window)
    {
        window.AllowIsolatedLibraryMonitoring = true;
        window.Width = 1280; window.Height = 800; window.WindowState = WindowState.Normal;
        window.PrepareBrowseSmokeTree(_mediaRoot);
        window.PropertyChanged += OnRowsChanged;
        window.BrowseSmokeEmptyState.IsVisibleChanged += OnEmptyChanged;
        window.Closed += (_, _) =>
        {
            _writersDrained = window.BrowseSmokeWritersDrained;
            _closed = true;
            _mediaPause?.Dispose(); _mediaPause = null;
        };
        _elapsed.Start();
    }

    public async Task ObserveAndCloseAsync(MainWindow window)
    {
        _phase = "startup-empty-parent";
        var initial = await window.InitialCatalogReady.WaitAsync(TimeSpan.FromSeconds(15));
        if (initial is not null) throw new InvalidOperationException("Browse startup projection failed.", initial);
        await SettleAsync(window, _parent, 0, false);
        Require(window.BrowseSmokeMonitorReady && window.BrowseSmokeEmptyState.IsVisible, "Empty parent or production monitor did not initialize.");

        // The catalog/cache are excluded watcher roots. This real media event in a
        // separate root proves that monitoring actually runs, not just its UI label.
        var witness = Path.Combine(_proof, "watcher-witness.png");
        _phase = "real-watcher-off-view-media";
        var emptyRows = window.PhotoRows;
        var emptyPublications = _rowPublications;
        var emptyTransitions = _emptyTransitions;
        await Task.Run(() => WritePng(witness));
        _originalHashes[witness] = SHA256.HashData(await File.ReadAllBytesAsync(witness));
        var store = new SqliteDesktopCatalogStore();
        await UntilAsync(async () => await store.GetItemAsync(witness) is not null, "Production watcher did not index a new synthetic PNG.");
        _monitorObserved = true;
        await SettleAsync(window, _parent, 0, false);
        Require(ReferenceEquals(emptyRows, window.PhotoRows) && _rowPublications == emptyPublications && _emptyTransitions == emptyTransitions,
            "An off-view media update reset or flashed the empty parent.");
        await VerifyNoiseStabilityAsync(window, "empty-parent", _parent, 0);

        _phase = "direct-folder-preview";
        var firstPreview = Stopwatch.StartNew();
        window.BrowseSmokeSelectFolder(_pictures);
        await UntilAsync(() => Task.FromResult(window.BrowseSmokeItemCount == 1 &&
            string.Equals(window.BrowseSmokePublishedFolder, _pictures, StringComparison.OrdinalIgnoreCase) &&
            window.BrowseSmokeDecodedPixels(_png)), "Direct PNG preview did not decode within 10 seconds.");
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        _firstPreviewMilliseconds = firstPreview.Elapsed.TotalMilliseconds;
        Require(_firstPreviewMilliseconds <= 10000, "Selection-to-decoded-thumbnail exceeded 10 seconds.");
        await SettleAsync(window, _pictures, 1, false);
        Require(window.BrowseSmokeLoadedPaths.SequenceEqual([_png], StringComparer.OrdinalIgnoreCase), "Nonrecursive folder contains the wrong files.");
        _directVerified = true;

        _phase = "fresh-folder-discovery";
        Require(await store.GetItemAsync(_freshPng) is null, "Fresh browse fixture was indexed before selection.");
        var discovery = Stopwatch.StartNew();
        window.BrowseSmokeSelectFolder(_freshFolder);
        await UntilAsync(() => Task.FromResult(window.BrowseSmokeItemCount == 1 &&
            string.Equals(window.BrowseSmokePublishedFolder, _freshFolder, StringComparison.OrdinalIgnoreCase) &&
            window.BrowseSmokeDecodedPixels(_freshPng)), "First browse did not discover and decode its unindexed direct PNG within 10 seconds.");
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        _firstDiscoveryMilliseconds = discovery.Elapsed.TotalMilliseconds;
        Require(_firstDiscoveryMilliseconds <= 10000 && await store.GetItemAsync(_freshPng) is not null,
            "First browse did not persist its discovered PNG within the first-preview deadline.");
        await SettleAsync(window, _freshFolder, 1, false);
        _freshDiscoveryVerified = true;
        window.BrowseSmokeSelectFolder(_pictures);
        await SettleAsync(window, _pictures, 1, false);

        _phase = "checkbox-subtree";
        Require(window.BrowseSmokeClickInclusion(_pictures) == false, "Checkbox did not exclude the chosen subtree immediately.");
        await SettleAsync(window, _pictures, 1, false);
        Require(window.BrowseSmokeClickInclusion(_pictures) == true && window.BrowseSmokeRootIsMixed,
            "Checkbox did not include only the chosen subtree.");
        await SettleAsync(window, _pictures, 1, false);
        _checkboxVerified = true;

        _phase = "recursive-toggle";
        window.BrowseSmokeSetSubfolders(true);
        await SettleAsync(window, _pictures, 2, true);
        Require(window.BrowseSmokeLoadedPaths.Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(new[] { _png, _video }.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            "Recursive folder did not include exactly the direct PNG and nested video.");
        _recursiveVerified = true;
        window.BrowseSmokeSetSubfolders(false);
        await SettleAsync(window, _pictures, 1, false);

        _phase = "rapid-folder-supersession";
        _expectedPublicationFolder = _pictures;
        window.BrowseSmokeSelectFolder(_parent);
        window.BrowseSmokeSelectFolder(_pictures);
        await SettleAsync(window, _pictures, 1, false);
        Require(_wrongFolderPublications == 0 && window.BrowseSmokeDecodedPixels(_png), "A superseded folder replaced the final selection.");
        _expectedPublicationFolder = null;
        _rapidVerified = true;
        await VerifyNoiseStabilityAsync(window, "direct-photo", _pictures, 1);

        _phase = "original-byte-verification";
        await Task.Run(() =>
        {
            foreach (var pair in _originalHashes)
                Require(File.Exists(pair.Key) && pair.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(pair.Key))), "Synthetic original bytes changed.");
        });
        _hashesVerified = true;
        Require(ErrorReporter.ErrorCount == 0, "Browse smoke wrote application errors.");
        ChecksPassed = true;
        // Await actual decoder readers before the production close drains all writers.
        _mediaPause = await AsyncMediaImage.PauseForFileOperationsAsync();
        _readersDrained = true;
        _phase = "production-shutdown";
        _closeRequested = true;
        window.Close();
    }

    private async Task VerifyNoiseStabilityAsync(MainWindow window, string phase, string folder, int count)
    {
        _phase = "noise-" + phase;
        await SettleAsync(window, folder, count, false);
        var rows = window.PhotoRows; var publications = _rowPublications; var transitions = _emptyTransitions;
        var generation = window.BrowseSmokeMonitorGeneration;
        var noise = Path.Combine(_proof, phase);
        await Task.Run(() =>
        {
            for (var index = 0; index < NoiseDirectoriesPerPhase; index++)
            {
                var directory = Path.Combine(noise, "dir-" + index.ToString("D4"));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "ignored.log"), "synthetic nonmedia watcher noise");
            }
        });
        await SettleAsync(window, folder, count, false);
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime; var quiet = Stopwatch.StartNew();
        while (quiet.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(50);
            Require(ReferenceEquals(rows, window.PhotoRows) && _rowPublications == publications && _emptyTransitions == transitions,
                "Nonmedia/off-view events repeatedly reset the grid or flashed EmptyState.");
            Require(!window.BrowseSmokeBusy && window.BrowseSmokeMonitorGeneration == generation, "Background work did not remain settled after noise.");
        }
        process.Refresh();
        _stability.Add(new { phase, noiseDirectories = NoiseDirectoriesPerPhase, noiseFiles = NoiseDirectoriesPerPhase,
            rowPublications = _rowPublications - publications, emptyTransitions = _emptyTransitions - transitions,
            monitorGenerationChanges = window.BrowseSmokeMonitorGeneration - generation,
            quietWallMs = quiet.Elapsed.TotalMilliseconds, quietCpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds });
    }

    private static async Task SettleAsync(MainWindow window, string folder, int count, bool recursive)
    {
        var stable = Stopwatch.StartNew(); object? rows = null;
        await UntilAsync(() =>
        {
            var ready = window.BrowseSmokeMonitorReady && !window.BrowseSmokeBusy && window.BrowseSmokeItemCount == count &&
                window.BrowseSmokeRecursive == recursive && string.Equals(window.BrowseSmokePublishedFolder, folder, StringComparison.OrdinalIgnoreCase);
            if (!ready || !ReferenceEquals(rows, window.PhotoRows)) { rows = window.PhotoRows; stable.Restart(); }
            return Task.FromResult(ready && stable.Elapsed >= TimeSpan.FromMilliseconds(800));
        }, "Browse/monitor projection did not settle at the expected folder/count.", 15);
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string failure, int seconds = 10)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            Require(clock.Elapsed < TimeSpan.FromSeconds(seconds) && ErrorReporter.ErrorCount == 0, failure);
            await Task.Delay(25);
        }
    }

    private void OnRowsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainWindow.PhotoRows)) return;
        _rowPublications++;
        if (_expectedPublicationFolder is not null && sender is MainWindow window && window.PhotoRows is VirtualPhotoRows &&
            !string.Equals(window.BrowseSmokePublishedFolder, _expectedPublicationFolder, StringComparison.OrdinalIgnoreCase)) _wrongFolderPublications++;
    }
    private void OnEmptyChanged(object sender, DependencyPropertyChangedEventArgs args) => _emptyTransitions++;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void WritePng(string path)
    {
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 }, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    public object CreateReport(int exitCode) => new
    {
        status = exitCode == 0 && ChecksPassed && GracefulExit && ErrorReporter.ErrorCount == 0 ? "passed" : "failed",
        check = "ui-browse-smoke", phase = _phase, version = ErrorReporter.Version,
        catalogRoot = LocalCatalogStore.CatalogDirectory, logRoot = ErrorReporter.LogRoot, mediaRoot = _mediaRoot,
        isolatedCatalog = LocalCatalogStore.IsIsolatedSmokeCatalog, monitoringObserved = _monitorObserved,
        firstDecodedPreviewMs = _firstPreviewMilliseconds, directCountVerified = _directVerified,
        firstDiscoveryMs = _firstDiscoveryMilliseconds, freshDiscoveryVerified = _freshDiscoveryVerified,
        recursiveCountVerified = _recursiveVerified, checkboxVerified = _checkboxVerified, rapidSelectionVerified = _rapidVerified,
        wrongFolderPublications = _wrongFolderPublications, rowPublications = _rowPublications, emptyTransitions = _emptyTransitions,
        stability = _stability, originalHashesVerified = _hashesVerified, originalsChecked = _originalHashes.Count,
        decoderReadersDrained = _readersDrained, catalogWritersDrained = _writersDrained, gracefulExit = GracefulExit,
        elapsedMs = _elapsed.Elapsed.TotalMilliseconds, errorCount = ErrorReporter.ErrorCount, exitCode,
        scope = "Actual production handlers, WPF decoded PNG, SQLite, metadata and filesystem monitoring; synthetic files only. Video count, not video decoding. CPU is observational; no physical input/DPI matrix."
    };
}
