// Compiled ONLY into a disposable source copy by prepare_windows_acceptance.py.
// The production Desktop project does not include this file.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Background;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Metadata;
using PhotoShelf.Infrastructure.Sqlite;
using SkiaSharp;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Image = System.Windows.Controls.Image;
using ListBox = System.Windows.Controls.ListBox;
using RadioButton = System.Windows.Controls.RadioButton;

namespace PhotoShelf.Desktop;

internal static class WindowsAcceptance
{
    internal static readonly bool Enabled = true;
    private const string Root = __OWNED_ROOT__;
    private const string OwnerToken = __OWNER_TOKEN__;
    private const int VirtualCount = 100_000;
    private static string Catalog => Path.Combine(Root, "catalog");
    private static string Media => Path.Combine(Root, "media");
    private static readonly ConcurrentQueue<double> WriteTimes = new();
    private static readonly ConcurrentDictionary<string, int> Reads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<double> DecodeTimes = new();
    private static readonly List<double> PageTimes = [];
    private static readonly List<object> ScopeResults = [];
    private static int _scrollsDuringIndexing, _maxQueue, _maxCachedPages;
    private static long _maxMemory, _warmMemory;
    private static double _maxGap, _lastTick;
    private static string _phase = "startup";

    internal static void RecordWrite(double milliseconds) => WriteTimes.Enqueue(milliseconds);

    internal static int Run(string[] args)
    {
        try
        {
            ValidateOwnership();
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows runtime required.");
            PinOwnedCatalog();
            if (args.Length == 0) return ParentAsync().GetAwaiter().GetResult();
            if (args.Length != 1 || args[0] is not ("first" or "restart" or "changed"))
                throw new ArgumentException("Only fixed acceptance phases are allowed; no paths are accepted.");
            return Child(args[0]);
        }
        catch (Exception error)
        {
            // No arbitrary path/exception dump. Owned evidence is retained on every failure.
            WriteNew(Path.Combine(Root, "failure.json"),
                new { status = "failed", phase = _phase, errorType = error.GetType().Name, frames = FailureFrames(error),
                    assertion = error is InvalidDataException or TimeoutException ? error.Message : null });
            return 1;
        }
    }

    private static void ValidateOwnership()
    {
        var canonical = Path.GetFullPath(Root);
        if (canonical != Root || Path.GetFileName(Root) != "PhotoShelf-acceptance-" + OwnerToken ||
            !string.Equals(Path.GetDirectoryName(Root), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Acceptance root is not its fresh temporary workspace.");
        RejectReparse(Root);
        var marker = Path.Combine(Root, "owner.json");
        RejectReparse(marker);
        if (new FileInfo(marker).Length > 1024) throw new InvalidDataException("Ownership marker is oversized.");
        using var owner = JsonDocument.Parse(File.ReadAllBytes(marker));
        Require(owner.RootElement.GetProperty("schema").GetInt32() == 1 &&
            owner.RootElement.GetProperty("token").GetString() == OwnerToken, "Ownership marker mismatch.");
        var executable = Path.GetFullPath(Environment.ProcessPath!);
        Require(executable.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Only the owned test executable may enter acceptance.");
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Reparse paths are not accepted by the test workspace.");
    }

    private sealed record Snapshot(string Hash, long Size, long Modified);
    private static IEnumerable<string> OwnedFiles(string directory)
    {
        RejectReparse(directory);
        foreach (var file in Directory.EnumerateFiles(directory)) { RejectReparse(file); yield return file; }
        foreach (var child in Directory.EnumerateDirectories(directory))
            foreach (var file in OwnedFiles(child)) yield return file;
    }
    private static Dictionary<string, Snapshot> SnapshotOriginals() => OwnedFiles(Media)
        .ToDictionary(path => Path.GetRelativePath(Media, path), path =>
        {
            RejectReparse(path); var info = new FileInfo(path);
            return new Snapshot(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), info.Length, info.LastWriteTimeUtc.Ticks);
        }, StringComparer.OrdinalIgnoreCase);

    private static void VerifyOriginals(Dictionary<string, Snapshot> expected)
    {
        var actual = SnapshotOriginals();
        Require(actual.Count == expected.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value),
            "Read-only scenario changed an original, companion, size or timestamp.");
    }

    private static async Task<int> ParentAsync()
    {
        _phase = "seed";
        Directory.CreateDirectory(Catalog); Directory.CreateDirectory(Media);
        await SeedAsync();
        var originals = SnapshotOriginals();
        var store = new SqliteDesktopCatalogStore(Catalog);
        var items = new List<SavedMediaItem>();
        await foreach (var item in store.EnumerateAsync(new CatalogViewQuery { Folder = Media, IncludeSubfolders = true, IncludeSystemFolders = true }))
            items.Add(item);
        var results = new List<JsonElement>();
        results.Add(await StartChildAsync("first"));
        VerifyOriginals(originals); await VerifyCatalogAsync(items);
        var cached = await CacheSnapshotAsync();
        Require(cached.Count >= 190, "Initial fingerprints are missing.");
        results.Add(await StartChildAsync("restart"));
        VerifyOriginals(originals); await VerifyCatalogAsync(items);
        Require(cached.SequenceEqual(await CacheSnapshotAsync()), "Restart changed the persisted fingerprint cache.");

        // One intentional mutation belongs to the fixture, outside the read-only phases.
        var changed = Path.Combine(Media, "A", "group-00-copy.png");
        var before = (await store.GetItemAsync(changed))!;
        WriteImage(changed, 201, SKEncodedImageFormat.Png, overwriteOwnedFixture: true);
        File.SetLastWriteTimeUtc(changed, before.FileModifiedAt!.Value.ToUniversalTime().AddSeconds(2));
        var observed = LibraryCatalogSynchronizer.Available(new FileSystemObservationProbe().ProbeFile(changed));
        Require(await store.ApplyObservationAsync(new(observed), before), "Changed fixture observation was rejected.");
        originals = SnapshotOriginals();
        results.Add(await StartChildAsync("changed"));
        VerifyOriginals(originals); await VerifyCatalogAsync(items);
        var after = await CacheSnapshotAsync();
        var altered = cached.Where(pair => !after.TryGetValue(pair.Key, out var value) || value != pair.Value).Select(pair => pair.Key).ToArray();
        Require(after.Count == cached.Count && altered.Length == 1 && altered[0] == before.AssetId,
            "Only the deliberately changed fixture may refresh its fingerprint.");
        WriteNew(Path.Combine(Root, "acceptance.json"), new
        {
            schema = 1, status = "passed", scope = "production-WPF-startup-in-owned-compile-time-test-copy",
            commit = "__SOURCE_COMMIT__", sourceCopyInstrumented = true, shippingEntrypointChanged = false,
            originalHashesSizesAndTimesPreserved = true, companionsPreserved = true, catalogIntegrityPassed = true,
            assetIdsAndFavoritesPreserved = true, restartCacheVerified = true, singleChangedFileVerified = true,
            syntheticCatalogRows = VirtualCount, originalsChecked = originals.Count, phases = results,
            limitations = new[] { "Test entrypoint bypasses updater admission; App startup and real MainWindow are used.",
                "100k absent catalog rows are fixture setup, not a media-import benchmark.",
                "A compile-time observer adds 90ms latency to first-pass fingerprint reads to guarantee overlap.",
                "Changed-file observation uses the production observation API; filesystem notification detection is covered separately.",
                "Video is a valid synthetic MP4: admission and byte preservation only, no playback claim.",
                "No physical sleep, device disconnect, power loss, file organization or quarantine execution is tested." }
        });
        return 0;
    }

    private static async Task<JsonElement> StartChildAsync(string phase)
    {
        _phase = phase;
        using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { phase } })
            ?? throw new IOException("Child process did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            // Only this handle, just created from the compile-time owned executable.
            process.Kill(); await process.WaitForExitAsync();
            throw new TimeoutException("Owned acceptance child exceeded its deadline.");
        }
        Require(process.ExitCode == 0, "Acceptance child failed.");
        var path = Path.Combine(Root, phase + ".json"); RejectReparse(path);
        Require(new FileInfo(path).Length < 1024 * 1024, "Child report exceeded its budget.");
        using var report = JsonDocument.Parse(File.ReadAllBytes(path));
        Require(report.RootElement.GetProperty("status").GetString() == "passed", "Child did not finish its assertions.");
        Require(report.RootElement.GetProperty("processId").GetInt32() == process.Id, "Child report does not identify the launched process.");
        return report.RootElement.Clone();
    }

    private static async Task SeedAsync()
    {
        var a = Directory.CreateDirectory(Path.Combine(Media, "A")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(Media, "B")).FullName;
        for (var group = 0; group < 33; group++)
        {
            var folder = group < 17 ? a : b;
            var original = Path.Combine(folder, $"group-{group:D2}.png");
            WriteImage(original, group + 1, SKEncodedImageFormat.Png);
            File.Copy(original, Path.Combine(folder, $"group-{group:D2}-copy.png"), false);
        }
        var bulk = Path.Combine(a, "bulk-000.png"); WriteImage(bulk, 100, SKEncodedImageFormat.Png);
        for (var index = 1; index < 130; index++) File.Copy(bulk, Path.Combine(a, $"bulk-{index:D3}.png"), false);
        WriteImage(Path.Combine(b, "fixture.jpg"), 150, SKEncodedImageFormat.Jpeg);
        WriteImage(Path.Combine(b, "fixture.webp"), 151, SKEncodedImageFormat.Webp);
        CopyResource("PhotoShelf.SyntheticHeif.heic", Path.Combine(b, "fixture.heic"));
        CopyResource("PhotoShelf.AcceptanceVideo.mp4", Path.Combine(b, "synthetic.mp4"));
        File.WriteAllText(Path.Combine(b, "fixture.xmp"), "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>");
        File.WriteAllText(Path.Combine(b, "fixture.aae"), "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict/></plist>");
        _phase = "seed-catalog";
        var store = new SqliteDesktopCatalogStore(Catalog); await store.InitializeAsync();
        _phase = "seed-fingerprints";
        await new PerceptualFingerprintStore(Catalog).InitializeAsync();
        _phase = "seed-preferences";
        await store.SaveAsync(new LocalCatalogState { BackgroundProcessingPaused = true,
            IncludeSystemFolders = true, ShowVideos = true, WatchedFolders = [Media], ViewMode = "All", DateGroupingMode = "FileDate" }, saveItems: false);
        _phase = "seed-media";
        var items = OwnedFiles(Media)
            .Where(path => Path.GetExtension(path) is not (".xmp" or ".aae"))
            .Select(path =>
            {
                var value = LibraryCatalogSynchronizer.Available(new FileSystemObservationProbe().ProbeFile(path));
                value.MetadataIndexed = true; value.MetadataStatus = MetadataReadStatus.Absent;
                value.IsVideo = Path.GetExtension(path) == ".mp4"; value.IsFavorite = path == bulk;
                return value;
            }).ToArray();
        await store.UpsertItemsAsync(items);
        _phase = "seed-large-catalog";
        await using var db = await OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA synchronous=FULL; PRAGMA cache_size=-65536;
            WITH digits(n) AS (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)), numbers(i) AS (
              SELECT a.n+10*b.n+100*c.n+1000*d.n+10000*e.n FROM digits a,digits b,digits c,digits d,digits e),
            fixture AS (SELECT i,$root || '\' || printf('%06d',i) || '.jpg' AS path FROM numbers)
            INSERT INTO desktop_media_items(asset_id,path,path_key,folder_key,search_key,is_favorite,size_bytes,
              file_modified_utc_ticks,file_local_ticks,file_month,is_video,metadata_indexed,metadata_status,last_seen_utc,availability)
            SELECT 'virtual-' || printf('%032x',i),path,upper(replace(path,'\','/')),upper(replace($root,'\','/')),
              upper(replace(path,'\','/')),0,10000+i,$ticks,$ticks,'2020-01',0,1,2,'2026-01-01T00:00:00Z',1 FROM fixture;
            """;
        command.Parameters.AddWithValue("$root", Path.Combine(Root, "absent-catalog-fixture"));
        command.Parameters.AddWithValue("$ticks", new DateTime(2020, 1, 1).Ticks);
        await command.ExecuteNonQueryAsync();
    }

    private static void CopyResource(string resource, string path)
    {
        using var source = typeof(MainWindow).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException("Synthetic embedded fixture missing.");
        using var dest = new FileStream(path, FileMode.CreateNew, FileAccess.Write); source.CopyTo(dest);
    }

    private static void WriteImage(string path, int pattern, SKEncodedImageFormat format, bool overwriteOwnedFixture = false)
    {
        using var bitmap = new SKBitmap(32, 32);
        for (var y = 0; y < 32; y++) for (var x = 0; x < 32; x++)
            bitmap.SetPixel(x, y, new SKColor((byte)(pattern * 7 + x * pattern), (byte)(y * 9 + pattern), (byte)(x * y + pattern * 3)));
        using var image = SKImage.FromBitmap(bitmap); using var bytes = image.Encode(format, 90);
        using var file = new FileStream(path, overwriteOwnedFixture ? FileMode.Truncate : FileMode.CreateNew, FileAccess.Write);
        bytes.SaveTo(file); file.Flush(true);
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Catalog, "catalog-v2.sqlite"), Pooling = false }.ToString());
        await db.OpenAsync(); return db;
    }

    private static async Task VerifyCatalogAsync(List<SavedMediaItem> expected)
    {
        var store = new SqliteDesktopCatalogStore(Catalog);
        foreach (var before in expected)
        {
            var after = await store.GetItemAsync(before.Path);
            Require(after is not null && after.AssetId == before.AssetId && after.IsFavorite == before.IsFavorite,
                "Acceptance lost a catalog identity or favorite.");
        }
        await using var db = await OpenAsync(); await using var command = db.CreateCommand();
        command.CommandText = "PRAGMA quick_check;"; Require((string?)await command.ExecuteScalarAsync() == "ok", "Catalog integrity check failed.");
        command.CommandText = "SELECT count(*) FROM desktop_media_items WHERE asset_id LIKE 'virtual-%' AND is_quarantined=0;";
        Require(Convert.ToInt64(await command.ExecuteScalarAsync()) == VirtualCount, "Synthetic catalog rows changed.");
        command.CommandText = "SELECT count(*) FROM desktop_media_items WHERE is_quarantined<>0;";
        Require(Convert.ToInt64(await command.ExecuteScalarAsync()) == 0, "Read-only acceptance executed quarantine.");
    }

    private static async Task<SortedDictionary<string, string>> CacheSnapshotAsync()
    {
        await using var db = await OpenAsync(); await using var command = db.CreateCommand();
        command.CommandText = "SELECT asset_id,path,path_key,size_bytes,file_modified_utc_ticks,observation_version,algorithm_version,status,difference_hash,average_hash,pixel_width,pixel_height,attempted_at_utc_ticks,retry_at_utc_ticks,error_code FROM perceptual_fingerprint_cache ORDER BY asset_id;";
        var result = new SortedDictionary<string, string>(); await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0), JsonSerializer.Serialize(Enumerable.Range(1, reader.FieldCount - 1).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray()));
        return result;
    }

    private static void PinOwnedCatalog()
    {
        // No shipping API accepts an existing catalog for smoke. Reflection is confined to
        // this compile-time owned copy before any App, cache or catalog initialization.
        var location = LocalCatalogStore.StorageLocation;
        typeof(CatalogLocation).GetField("_directory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(location, Catalog);
        typeof(CatalogLocation).GetProperty(nameof(CatalogLocation.IsIsolatedSmoke))!.SetValue(location, true);
        Require(LocalCatalogStore.IsIsolatedSmokeCatalog && LocalCatalogStore.CatalogDirectory == Catalog, "Isolation failed.");
    }

    private static int Child(string phase)
    {
        RejectReparse(Catalog); RejectReparse(Media);
        ErrorReporter.AutomatedCheck = true;
        var app = new App(); app.InitializeComponent();
        var clock = Stopwatch.StartNew(); _lastTick = 0;
        var pulse = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(100) };
        pulse.Tick += (_, _) => { var now = clock.Elapsed.TotalMilliseconds; _maxGap = Math.Max(_maxGap, now - _lastTick); _lastTick = now; };
        var observer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        MainWindow? testedWindow = null;
        var checksPassed = false;
        var closedCleanly = false;
        observer.Tick += async (_, _) =>
        {
            if (app.MainWindow is not MainWindow window || !window.IsLoaded) return;
            observer.Stop();
            try
            {
                await ExerciseAsync(window, phase);
                testedWindow = window; checksPassed = true;
                window.Closed += (_, _) => closedCleanly = window.BrowseSmokeWritersDrained;
                window.Close();
            }
            catch (Exception error)
            {
                WriteNew(Path.Combine(Root, phase + ".json"), new { status = "failed", phase = _phase, errorType = error.GetType().Name, frames = FailureFrames(error),
                    assertion = error is InvalidDataException or TimeoutException ? error.Message : null,
                    decoderCalls = Reads.Values.Sum(), completedDuplicateScopes = ScopeResults,
                    viewCount = window.BrowseSmokeItemCount, busy = window.BrowseSmokeBusy });
                app.Shutdown(1);
            }
        };
        pulse.Start(); observer.Start();
        var exit = app.Run(); pulse.Stop(); observer.Stop();
        if (!checksPassed || !closedCleanly || testedWindow is null || ErrorReporter.ErrorCount != 0 || exit != 0) return 1;
        WriteNew(Path.Combine(Root, phase + ".json"), new
        {
            status = "passed", phase, processId = Environment.ProcessId, gracefulExit = true,
            reads = Reads.OrderBy(pair => pair.Key).Select(pair => new { name = pair.Key, count = pair.Value }).ToArray(),
            decoderCalls = Reads.Values.Sum(), decoderTimes = Summary(DecodeTimes), sqliteWriteTimes = Summary(WriteTimes),
            uiPageTimes = Summary(PageTimes), scrollsDuringIndexing = _scrollsDuringIndexing,
            maxDispatcherGapMs = _maxGap, maxPrivateBytes = _maxMemory, warmPrivateBytes = _warmMemory, maxQueue = _maxQueue, maxCachedPages = _maxCachedPages,
            duplicateScopes = ScopeResults, noApplicationErrors = true, actualCatalogStartup = true,
            videoPlaybackTested = false, physicalSleepTested = false
        });
        return 0;
    }

    private static async Task ExerciseAsync(MainWindow window, string phase)
    {
        _phase = "initial-projection";
        var startupError = await window.InitialCatalogReady.WaitAsync(TimeSpan.FromSeconds(30));
        Require(startupError is null, "Production catalog startup did not complete successfully.");
        await UntilAsync(() => window.PhotoRows is VirtualPhotoRows rows && rows.ItemCount > 0 && !Get<bool>(window, "_isCatalogLoading"));
        if (phase == "first") Require(((VirtualPhotoRows)window.PhotoRows).ItemCount >= VirtualCount, "Large catalog did not restore its initial view.");
        Require(window.GetBackgroundActivity().Paused, "Persisted pause was not restored before workers.");
        var original = window.FingerprintReader;
        window.FingerprintReader = (path, token) =>
        {
            Require(path.StartsWith(Media + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Reader escaped its synthetic media root.");
            Reads.AddOrUpdate(Path.GetRelativePath(Media, path), 1, (_, count) => count + 1);
            if (phase == "first" && token.WaitHandle.WaitOne(90)) token.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp(); var result = original(path, token);
            DecodeTimes.Enqueue(Stopwatch.GetElapsedTime(started).TotalMilliseconds); return result;
        };
        await window.ToggleBackgroundProcessingAsync();
        if (phase == "first")
        {
            _phase = "large-view-during-indexing";
            await UntilAsync(() => Reads.Count > 0 && !Get<Task>(window, "_fingerprintTask").IsCompleted);
            await UntilAsync(() => Descendants<Image>(window).Any(image => image.IsVisible &&
                AsyncMediaImage.GetPath(image) is { } path && path.StartsWith(Media + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                image.Source is BitmapSource { PixelWidth: > 0 }));
            for (var i = 0; i < 24; i++)
            {
                var rows = (VirtualPhotoRows)window.PhotoRows;
                var started = Stopwatch.GetTimestamp();
                if (!Get<Task>(window, "_fingerprintTask").IsCompleted) _scrollsDuringIndexing++;
                var target = i % 2 == 0 ? VirtualCount - 100 : 0;
                window.BrowseSmokeScrollToItem(target);
                await UntilAsync(() => rows.LoadedItems.Any(item => item.ViewIndex == target));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                PageTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                await Task.Delay(100); // Outside the event-to-bound-page measurement.
                _maxCachedPages = Math.Max(_maxCachedPages, rows.CachedPageCount);
                _maxQueue = Math.Max(_maxQueue, BackgroundWorkScheduler.Shared.PendingCount);
                using var process = Process.GetCurrentProcess();
                if (i == 3) _warmMemory = process.PrivateMemorySize64;
                if (i >= 3)
                {
                    _maxMemory = Math.Max(_maxMemory, process.PrivateMemorySize64);
                    Require(_maxMemory - _warmMemory <= 256L * 1024 * 1024, "Large-view memory grew beyond the warm regression budget.");
                }
                Require(rows.CachedPageCount <= 12 && rows.CachedRowCount < 2048, "Large view exceeded the bounded viewport cache.");
                Require(_maxGap < 5000, "Dispatcher stalled for five seconds.");
                Require(_maxQueue <= 256, "Scheduler queue exceeded its production capacity.");
            }
            Require(_scrollsDuringIndexing >= 8, "Large view did not overlap real fingerprint indexing.");
        }
        await UntilAsync(() => Get<Task>(window, "_fingerprintTask").IsCompleted && Get<Task>(window, "_metadataTask").IsCompleted, 90);
        await Task.Delay(300); // Observe a queued follow-up, not just the predecessor's completion.
        var beforePause = Reads.Values.Sum();
        await window.ToggleBackgroundProcessingAsync();
        Require(window.BrowseSmokeReadersDrained, "Pause returned before real readers drained.");
        await window.ToggleBackgroundProcessingAsync();
        await UntilAsync(() => Get<Task>(window, "_fingerprintTask").IsCompleted && Get<Task>(window, "_metadataTask").IsCompleted, 60);
        await Task.Delay(300);
        Require(Reads.Values.Sum() == beforePause, "Pause/resume decoded unchanged media again.");
        if (phase == "restart") Require(Reads.Count == 0, "Restart ignored the persisted fingerprint cache.");
        if (phase == "changed") Require(Reads.Count == 1 && Reads.GetValueOrDefault(Path.Combine("A", "group-00-copy.png")) == 1,
            "Changed-file phase decoded more than its one altered original.");
        if (phase == "first")
        {
            Require(WriteTimes.Count > 0, "No real fingerprint catalog commit was measured.");
            Require(Reads.ContainsKey(Path.Combine("B", "fixture.jpg")) && Reads.ContainsKey(Path.Combine("B", "fixture.webp")) &&
                Reads.ContainsKey(Path.Combine("B", "fixture.heic")), "Mixed-format visual indexing was not exercised.");
            await DuplicateScopesAsync(window);
        }
        await window.ToggleBackgroundProcessingAsync();
        Require(window.GetBackgroundActivity().Paused && window.BrowseSmokeReadersDrained, "Final persisted pause failed.");
    }

    private static async Task DuplicateScopesAsync(MainWindow window)
    {
        _phase = "duplicate-folder-ready";
        // Use the real folder handler on one owned node; no disk ancestors are expanded.
        // Selecting a known owned node uses the production folder event; no guard is relaxed.
        window.AllowIsolatedLibraryMonitoring = true;
        var folder = new FolderNode(Path.Combine(Media, "A"));
        Invoke(window, "OnFolderTreeSelectedItemChanged", window.FindName("FolderTree"), new RoutedPropertyChangedEventArgs<object>(null!, folder));
        await UntilAsync(() => window.BrowseSmokePublishedFolder == folder.FullPath && window.BrowseSmokeItemCount > 130 && !window.BrowseSmokeBusy);
        foreach (var currentFolder in new[] { false, true })
        {
            _phase = currentFolder ? "duplicate-current-folder" : "duplicate-whole-library";
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handling = false; var dialogObserved = false;
            var scopeDeadline = Stopwatch.StartNew();
            var observer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            observer.Tick += async (_, _) =>
            {
                try
                {
                    if (scopeDeadline.Elapsed.TotalSeconds > 60) throw new TimeoutException("Duplicate modal scope timed out.");
                    if (!dialogObserved && System.Windows.Application.Current.Windows.OfType<DuplicateSearchDialog>().FirstOrDefault() is { } dialog)
                    {
                        dialogObserved = true;
                        var radio = (RadioButton)dialog.FindName(currentFolder ? "CurrentFolderRadio" : "WholeLibraryRadio");
                        Require(radio.IsEnabled, "Requested duplicate scope is unavailable."); radio.IsChecked = true;
                        Click(Descendants<Button>(dialog).Single(button => Equals(button.Content, "Начать")));
                    }
                    if (!handling && System.Windows.Application.Current.Windows.OfType<DuplicateReviewWindow>().FirstOrDefault() is { } review)
                    {
                        handling = true;
                        await ReviewAsync(review, currentFolder);
                        review.Close(); completion.TrySetResult();
                    }
                }
                catch (Exception error)
                {
                    observer.Stop();
                    completion.TrySetException(error);
                    // RaiseEvent can synchronously enter ShowDialog's nested dispatcher.
                    // Close only this test's duplicate modals so the failure can unwind.
                    foreach (var modal in System.Windows.Application.Current.Windows.Cast<Window>()
                        .Where(modal => modal is DuplicateSearchDialog or DuplicateReviewWindow).ToArray()) modal.Close();
                }
            };
            observer.Start();
            try
            {
                Click((Button)window.FindName("DuplicatesButton"));
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
                await UntilAsync(() => Get<object?>(window, "_duplicateCancellation") is null);
                Require(dialogObserved, "Production scope dialog was not observed.");
            }
            finally { observer.Stop(); }
        }
    }

    private static async Task ReviewAsync(DuplicateReviewWindow review, bool currentFolder)
    {
        await UntilAsync(() => review.Groups.Count > 0 && !Get<bool>(review, "_loadingPage"));
        var session = Get<DuplicateSearchSession>(review, "_session");
        Require(session.GroupCount == (currentFolder ? 18 : 34), "Scope returned an unexpected group snapshot.");
        var groups = (ListBox)review.FindName("GroupList"); var members = (ListBox)review.FindName("ItemList");
        while (!review.Groups.Any(group => group.TotalFiles == 130))
        {
            var previous = Get<long>(review, "_groupOffset");
            Click((Button)review.FindName("NextGroupsButton"));
            await UntilAsync(() => Get<long>(review, "_groupOffset") > previous && !Get<bool>(review, "_loadingPage"));
        }
        groups.SelectedItem = review.Groups.First(group => group.TotalFiles == 130);
        groups.ScrollIntoView(groups.SelectedItem);
        await UntilAsync(() => review.SelectedGroup is { TotalFiles: 130, Items.Count: 65 } && !Get<bool>(review, "_loadingPage"));
        var first = review.SelectedGroup!;
        Require(first.Items.Count == 65 && first.Items.Count(item => item.IsKeep) == 1, "Large duplicate group was not bounded.");
        members.ScrollIntoView(first.Items[^1]); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Click((Button)review.FindName("NextMembersButton"));
        await UntilAsync(() => review.SelectedGroup?.MemberOffset == 64 && !Get<bool>(review, "_loadingPage"));
        Click((Button)review.FindName("PreviousMembersButton"));
        await UntilAsync(() => review.SelectedGroup?.MemberOffset == 0 && !Get<bool>(review, "_loadingPage"));
        var expectedExtras = currentFolder ? 146 : 162;
        Click(Descendants<Button>(review).Single(button => Equals(button.Content, "Отметить все лишние")));
        await UntilAsync(() => !Get<bool>(review, "_editingSelection") && !Get<bool>(review, "_loadingPage"));
        Require((await session.ReadSelectionSummaryAsync()).SelectedFiles == expectedExtras, "Bulk UI marking did not apply to the whole snapshot.");
        var oldOffset = Get<long>(review, "_groupOffset");
        var forward = ((Button)review.FindName("NextGroupsButton")).IsEnabled;
        var expectedOffset = oldOffset + (forward ? 16 : -16);
        Click((Button)review.FindName(forward ? "NextGroupsButton" : "PreviousGroupsButton"));
        await UntilAsync(() => Get<long>(review, "_groupOffset") == expectedOffset && !Get<bool>(review, "_loadingPage"));
        Require(review.Groups.SelectMany(group => group.Items).All(item => item.IsKeep ? !item.IsSelected : item.IsSelected),
            "Offscreen marks were lost or a keeper was marked.");
        Click((Button)review.FindName(forward ? "PreviousGroupsButton" : "NextGroupsButton"));
        await UntilAsync(() => Get<long>(review, "_groupOffset") == oldOffset && !Get<bool>(review, "_loadingPage"));
        Click(Descendants<Button>(review).Single(button => Equals(button.Content, "Снять все отметки")));
        await UntilAsync(() => !Get<bool>(review, "_editingSelection") && !Get<bool>(review, "_loadingPage"));
        Require((await session.ReadSelectionSummaryAsync()).SelectedFiles == 0, "Clear-all did not clear the snapshot.");
        ScopeResults.Add(new { scope = currentFolder ? "current-folder" : "whole-library", groups = session.GroupCount,
            bulkMarked = expectedExtras, keeperProtected = true, groupPagingVerified = true, memberPagingVerified = true });
    }

    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Click(Button button) { Require(button.IsEnabled, "Production button was disabled."); button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button)); }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T value) yield return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
    private static async Task UntilAsync(Func<bool> predicate, int seconds = 30)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException("Acceptance phase timed out: " + _phase);
            await Task.Delay(25);
        }
    }
    private static object Summary(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        return new { count = values.Length, p95Ms = values.Length == 0 ? (double?)null : values[(int)Math.Ceiling(values.Length * .95) - 1],
            maxMs = values.Length == 0 ? (double?)null : values[^1] };
    }
    private static void WriteNew(string path, object value)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true }); file.Flush(true);
    }
    private static string[] FailureFrames(Exception error) => new StackTrace(error, false).GetFrames()
        .Take(8).Select(frame => frame.GetMethod()).Where(method => method is not null)
        .Select(method => method!.DeclaringType?.FullName + "." + method.Name).ToArray();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
