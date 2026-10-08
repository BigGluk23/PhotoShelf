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
internal sealed partial class UiBrowseSmokeSession
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
    private const int SearchSortFixtureCount = 96;
    private readonly List<SearchSortFixture> _searchSortFixtures = [];
    private readonly List<object> _searchSortCases = [];
    private string? _expectedPublicationSearch;
    private int _wrongSearchPublications, _searchSortAnchorChecks, _searchSortSelectionChecks, _searchSortHashesChecked;
    private bool _searchSortVerified, _searchSortHashesVerified, _searchSortRapidVerified, _searchSortDecoderVerified;
    private bool _backgroundPauseVerified, _backgroundPausePersisted, _backgroundResumeVerified, _backgroundIdleVerified;

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

        await VerifySearchSortAsync(window);
        await VerifyBackgroundPauseAsync(window);
        await VerifyBackgroundSoakAsync(window);

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

    private async Task VerifyBackgroundPauseAsync(MainWindow window)
    {
        _phase = "background-pause";
        var store = new SqliteDesktopCatalogStore();
        await UntilAsync(() => Task.FromResult(!window.BrowseSmokeBusy), "Background workers did not finish their initial queue.");
        Require(window.GetBackgroundActivity().Details.Contains("Метаданные") && window.GetBackgroundActivity().CanToggle,
            "Operations background activity is unavailable with an empty move history.");
        await window.ToggleBackgroundProcessingAsync();
        Require(window.GetBackgroundActivity().Paused && window.GetBackgroundActivity().CanToggle && !window.BrowseSmokeMonitoringEnabled,
            "Background pause returned before the monitor drained.");
        _backgroundPausePersisted = (await store.LoadAsync(includeItems: false)).BackgroundProcessingPaused;
        Require(_backgroundPausePersisted, "Background pause was not persisted in the isolated catalog.");
        var witness = Path.Combine(_proof, "created-while-paused.png");
        await Task.Run(() => WritePng(witness));
        var modified = File.GetLastWriteTimeUtc(witness);
        _originalHashes[witness] = SHA256.HashData(await File.ReadAllBytesAsync(witness));
        await Task.Delay(750);
        Require(await store.GetItemAsync(witness) is null, "A paused watcher still indexed a new original.");
        _backgroundPauseVerified = true;
        _phase = "background-resume";
        await window.ToggleBackgroundProcessingAsync();
        await UntilAsync(async () => await store.GetItemAsync(witness) is { MetadataIndexed: true } && !window.BrowseSmokeBusy,
            "Resume did not discover the queued PNG and finish its metadata.", 15);
        Require(!(await store.LoadAsync(includeItems: false)).BackgroundProcessingPaused && window.BrowseSmokeMonitoringEnabled,
            "Resume did not restore background processing and its persisted setting.");
        _backgroundResumeVerified = true;
        await Task.Delay(1000);
        Require(!window.BrowseSmokeBusy && File.GetLastWriteTimeUtc(witness) == modified,
            "Background work restarted without events or changed the original modification time.");
        _backgroundIdleVerified = true;
    }

    private async Task VerifySearchSortAsync(MainWindow window)
    {
        _phase = "search-sort-fixture";
        var folder = Path.Combine(_mediaRoot, "SearchSort");
        await Task.Run(() =>
        {
            Require(!Directory.Exists(folder), "Search/sort fixture directory already exists.");
            Directory.CreateDirectory(folder);
            for (var index = 0; index < SearchSortFixtureCount; index++)
            {
                var name = (index % 2 == 0 ? "p01-alpha-" : "p01-beta-") + index.ToString("D3") + ".png";
                var path = Path.Combine(folder, name);
                WritePng(path);
                // Twelve distinct local dates in one month, eight equal ticks each.
                // Path-ASC tie breaking must stay the same in both date directions.
                File.SetLastWriteTime(path, new DateTime(2024, 3, 1 + index / 8, 12, 0, 0, DateTimeKind.Local));
                _searchSortFixtures.Add(new(path, File.GetLastWriteTime(path), File.GetLastWriteTimeUtc(path),
                    SHA256.HashData(File.ReadAllBytes(path))));
            }
        });
        Require(_searchSortFixtures.Count == SearchSortFixtureCount && _searchSortFixtures.GroupBy(item => item.FileDate.Ticks).All(group => group.Count() == 8),
            "Search/sort fixture lost its equal-date tie cases.");
        window.BrowseSmokeSelectFolder(folder);
        await SettleAsync(window, folder, SearchSortFixtureCount, false);
        Require(window.BrowseSmokeNewestFirst, "Isolated search/sort fixture must start in the default descending date mode.");
        await VerifySearchSortOrderAsync(window, folder, "", true, "all-desc");
        await VerifySortReversalAsync(window, folder, "", false, "all-asc");
        await VerifySortReversalAsync(window, folder, "", true, "all-desc-restored");

        _phase = "search-alpha-desc";
        window.BrowseSmokeSearch("p01-alpha");
        await VerifySearchSortOrderAsync(window, folder, "p01-alpha", true, "search-desc");
        await VerifySortReversalAsync(window, folder, "p01-alpha", false, "search-asc");
        await VerifySortReversalAsync(window, folder, "p01-alpha", true, "search-desc-restored");

        _phase = "rapid-search-supersession";
        _expectedPublicationSearch = "p01-alpha";
        var publications = _rowPublications;
        // All three TextChanged events use the real production path. The obsolete
        // requests must never publish after the final input, including its debounce.
        window.BrowseSmokeSearch("");
        window.BrowseSmokeSearch("p01-beta");
        window.BrowseSmokeSearch("p01-alpha");
        await VerifySearchSortOrderAsync(window, folder, "p01-alpha", true, "rapid-final-search");
        Require(_wrongSearchPublications == 0 && _rowPublications > publications, "A superseded search published rows or the final search never published.");
        _expectedPublicationSearch = null;
        _searchSortRapidVerified = true;

        _phase = "search-sort-original-byte-verification";
        await Task.Run(() =>
        {
            foreach (var item in _searchSortFixtures)
            {
                Require(File.Exists(item.Path) && item.Sha256.SequenceEqual(SHA256.HashData(File.ReadAllBytes(item.Path))) &&
                    File.GetLastWriteTimeUtc(item.Path) == item.ModifiedUtc, "Search/sort changed a synthetic original's bytes or modification time.");
                _searchSortHashesChecked++;
            }
        });
        _searchSortHashesVerified = _searchSortHashesChecked == SearchSortFixtureCount;
        Require(_searchSortCases.Count == 7 && _searchSortSelectionChecks == 4 && _searchSortAnchorChecks == 4 &&
            _searchSortDecoderVerified && _searchSortHashesVerified && window.BrowseSmokeMonitoringEnabled,
            "Search/sort regression did not complete every required check with production monitoring enabled.");
        _searchSortVerified = true;
    }

    private async Task VerifySortReversalAsync(MainWindow window, string folder, string search, bool newestFirst, string scenario)
    {
        _phase = scenario;
        var items = window.BrowseSmokeSmallFixtureItems();
        Require(items.Length >= 48 && window.BrowseSmokeNewestFirst != newestFirst, "Sort reversal needs the complete small fixture in the opposite direction.");
        window.BrowseSmokeScrollToItem(items.Length / 2);
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        var anchor = window.BrowseSmokeAnchor;
        Require(anchor.Index > 0 && anchor.Path is not null, "Sort regression did not establish a noninitial viewport anchor.");
        var selected = window.BrowseSmokeSelectVisiblePhoto(anchor.Path!);
        await UntilAsync(() => Task.FromResult(window.BrowseSmokeDecodedPixels(selected.Path)), "Selected synthetic PNG did not decode before sorting.");
        window.BrowseSmokeReverseDateSort();
        await VerifySearchSortOrderAsync(window, folder, search, newestFirst, scenario);
        Require(window.BrowseSmokeSelectionRetained(selected), "Date reversal changed the selected PhotoItem identity or lost its selection.");
        _searchSortSelectionChecks++;
        // Reversing a date order can move the anchor within its photo row. Only
        // containment in the top visible row is required, never first-item equality.
        Require(window.BrowseSmokeTopRowPaths.Contains(anchor.Path!, StringComparer.OrdinalIgnoreCase),
            "Date reversal did not preserve the old path in the top visible photo row.");
        _searchSortAnchorChecks++;
        await UntilAsync(() => Task.FromResult(window.BrowseSmokeDecodedPixels(selected.Path)), "Selected synthetic PNG did not decode after sorting.");
    }

    private async Task VerifySearchSortOrderAsync(MainWindow window, string folder, string search, bool newestFirst, string scenario)
    {
        _phase = scenario;
        // Oracle uses fixture file dates and normalized paths, never a second call
        // to the SQL query under test. Literal substring semantics stay explicit.
        var filtered = _searchSortFixtures.Where(item => NormalizeSmokePath(item.Path).Contains(search.ToUpperInvariant(), StringComparison.Ordinal));
        var expected = (newestFirst ? filtered.OrderByDescending(item => item.FileDate.Ticks) : filtered.OrderBy(item => item.FileDate.Ticks))
            .ThenBy(item => NormalizeSmokePath(item.Path), StringComparer.Ordinal).Select(item => item.Path).ToArray();
        await UntilAsync(() => Task.FromResult(window.BrowseSmokePublishedSearch == search && window.BrowseSmokeNewestFirst == newestFirst &&
            string.Equals(window.BrowseSmokePublishedFolder, folder, StringComparison.OrdinalIgnoreCase)), "Search/date mode did not publish the final query.");
        await SettleAsync(window, folder, expected.Length, false);
        await UntilAsync(() => Task.FromResult(window.BrowseSmokeSmallFixtureItems().Length == expected.Length), "Small search/sort fixture pages did not finish loading.");
        var actual = window.BrowseSmokeSmallFixtureItems();
        Require(window.BrowseSmokePublishedSearch == search && window.BrowseSmokeNewestFirst == newestFirst && window.BrowseSmokeMonitoringEnabled,
            "Search/date query changed while validating its settled view, or monitoring was paused.");
        Require(actual.Select(item => item.Path).SequenceEqual(expected, StringComparer.OrdinalIgnoreCase) &&
            actual.Select(item => item.ViewIndex).SequenceEqual(Enumerable.Range(0, expected.Length).Select(index => (long)index)),
            "Search/sort result is incomplete, duplicated, or ordered differently from fixture dates and path-ASC ties.");
        var visible = window.BrowseSmokeTopRowPaths.FirstOrDefault();
        Require(visible is not null && expected.Contains(visible, StringComparer.OrdinalIgnoreCase), "Search/sort has no expected file in the visible row.");
        await UntilAsync(() => Task.FromResult(window.BrowseSmokeDecodedPixels(visible!)), "Search/sort visible PNG did not decode.");
        _searchSortDecoderVerified = true;
        _searchSortCases.Add(new { scenario, search, newestFirst, expectedCount = expected.Length, actualCount = actual.Length,
            fullOrderVerified = true, equalDateTiesVerified = true, decodedVisiblePng = true, monitoringEnabled = true });
    }

    private static string NormalizeSmokePath(string path) => path.Replace('/', '\\').ToUpperInvariant();
    private sealed record SearchSortFixture(string Path, DateTime FileDate, DateTime ModifiedUtc, byte[] Sha256);

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
        if (_expectedPublicationSearch is not null && sender is MainWindow searchWindow && searchWindow.PhotoRows is VirtualPhotoRows &&
            !string.Equals(searchWindow.BrowseSmokePublishedSearch, _expectedPublicationSearch, StringComparison.Ordinal)) _wrongSearchPublications++;
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
        background = new { pauseVerified = _backgroundPauseVerified, pausePersisted = _backgroundPausePersisted,
            resumeVerified = _backgroundResumeVerified, idleVerified = _backgroundIdleVerified },
        searchSort = new { passed = _searchSortVerified, fixtureCount = _searchSortFixtures.Count, hashesChecked = _searchSortHashesChecked,
            originalHashesVerified = _searchSortHashesVerified, modificationTimesVerified = _searchSortHashesVerified,
            selectionChecks = _searchSortSelectionChecks, anchorChecks = _searchSortAnchorChecks,
            rapidSearchVerified = _searchSortRapidVerified, wrongSearchPublications = _wrongSearchPublications,
            decodedPngVerified = _searchSortDecoderVerified, cases = _searchSortCases },
        backgroundSoak = _soakReport,
        decoderReadersDrained = _readersDrained, catalogWritersDrained = _writersDrained, gracefulExit = GracefulExit,
        elapsedMs = _elapsed.Elapsed.TotalMilliseconds, errorCount = ErrorReporter.ErrorCount, exitCode,
        scope = "Actual production handlers, WPF decoded PNG, SQLite, metadata and filesystem monitoring; synthetic files only. Video count, not video decoding. CPU is observational; no physical input/DPI matrix."
    };
}
