using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;
using PhotoShelf.Application.Background;
using PhotoShelf.Application.Catalog;
using SkiaSharp;

namespace PhotoShelf.Desktop;

internal sealed partial class UiBrowseSmokeSession
{
    private object? _soakReport;
    private async Task VerifyBackgroundSoakAsync(MainWindow window)
    {
        Require(LocalCatalogStore.IsIsolatedSmokeCatalog, "Soak must never use a personal catalog.");
        var raw = Environment.GetEnvironmentVariable("PHOTOSHELF_BACKGROUND_SOAK_SECONDS");
        var seconds = raw is null ? 30 : int.TryParse(raw, out var value) && value is >= 30 and <= 14400
            ? value : throw new InvalidOperationException("Soak duration must be 30..14400 seconds.");
        _phase = "background-soak";
        var root = Path.Combine(_proof, "soak");
        var fixtures = new Dictionary<string, (byte[] Hash, long Size, DateTime Modified)>();
        var reads = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var originalReader = window.FingerprintReader;
        var control = BackgroundWorkController.Shared;
        var originalMode = control.Mode;
        var elapsed = Stopwatch.StartNew();
        var lastTick = elapsed.Elapsed.TotalMilliseconds;
        double maxGap = 0;
        long maxMemory = 0, warmedMemory = 0;
        var cycles = 0; var samples = new Queue<object>();
        var pulse = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(100) };
        pulse.Tick += (_, _) => { var now = elapsed.Elapsed.TotalMilliseconds; maxGap = Math.Max(maxGap, now - lastTick); lastTick = now; };
        window.FingerprintReader = (path, token) =>
        {
            if (string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(path).StartsWith("event-", StringComparison.Ordinal))
                reads.AddOrUpdate(path, 1, (_, count) => count + 1);
            return originalReader(path, token);
        };
        pulse.Start();
        try
        {
            await window.ToggleBackgroundProcessingAsync();
            Require(window.GetBackgroundActivity().Paused, "Fixture setup must drain background readers.");
            await Task.Run(() =>
            {
                Directory.CreateDirectory(root);
                using var bitmap = new SKBitmap(16, 16); bitmap.Erase(SKColors.Red);
                using var image = SKImage.FromBitmap(bitmap);
                foreach (var format in new[] { SKEncodedImageFormat.Png, SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Webp })
                {
                    var path = Path.Combine(root, "fixture." + (format == SKEncodedImageFormat.Jpeg ? "jpg" : format.ToString().ToLowerInvariant()));
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var encoded = image.Encode(format, 90); encoded.SaveTo(file);
                }
                using (var source = typeof(MainWindow).Assembly.GetManifestResourceStream("PhotoShelf.SyntheticHeif.heic")!)
                using (var file = new FileStream(Path.Combine(root, "fixture.heic"), FileMode.CreateNew)) source.CopyTo(file);
                File.WriteAllBytes(Path.Combine(root, "broken.png"), [137, 80, 78, 71, 13, 10, 26, 10, 0, 1, 2]);
                foreach (var path in Directory.EnumerateFiles(root))
                    fixtures.Add(path, (SHA256.HashData(File.ReadAllBytes(path)), new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));
            });
            await window.ToggleBackgroundProcessingAsync();
            var store = new SqliteDesktopCatalogStore();
            await UntilAsync(async () => await store.GetItemAsync(Path.Combine(root, "fixture.heic")) is { MetadataIndexed: true } &&
                fixtures.Keys.All(path => reads.GetValueOrDefault(path) >= 1) && !window.BrowseSmokeBusy,
                "Soak fixtures did not finish background processing.", 40);
            // A predecessor may finish before its Dispatcher-posted refresh has started.
            // Wait for observable work on every fixture, then a sustained idle boundary.
            await Task.Delay(500);
            await UntilAsync(() => Task.FromResult(!window.BrowseSmokeBusy), "Initial visual indexing did not settle.", 30);
            Require(fixtures.Keys.All(path => reads.GetValueOrDefault(path) == 1),
                "Initial fingerprint counts: " + string.Join(", ", fixtures.Keys.Select(path => Path.GetFileName(path) + "=" + reads.GetValueOrDefault(path))));
            var originalIds = new Dictionary<string, string>();
            foreach (var path in fixtures.Keys) originalIds.Add(path, (await store.GetItemAsync(path))!.AssetId);
            elapsed.Restart(); lastTick = 0; maxGap = 0;
            do
            {
                control.Mode = (BackgroundLoadMode)(cycles % 3);
                control.SetPowerState(cycles % 2 == 0);
                control.NoteInteraction();
                // New real filesystem event while a different view is being navigated.
                var witness = Path.Combine(root, "event-" + cycles.ToString("D5") + ".png");
                await Task.Run(() => WritePng(witness));
                var expected = await Task.Run(() => (Hash: SHA256.HashData(File.ReadAllBytes(witness)),
                    Size: new FileInfo(witness).Length, Modified: File.GetLastWriteTimeUtc(witness)));
                fixtures.Add(witness, expected);
                window.BrowseSmokeSearch("");
                window.BrowseSmokeSelectFolder(_pictures);
                window.BrowseSmokeSelectFolder(Path.Combine(_mediaRoot, "SearchSort"));
                await SettleAsync(window, Path.Combine(_mediaRoot, "SearchSort"), SearchSortFixtureCount, false);
                window.BrowseSmokeScrollToItem(cycles % 2 == 0 ? 0 : 80);
                await UntilAsync(() => Task.FromResult(window.BrowseSmokeTopRowPaths.Any(window.BrowseSmokeDecodedPixels)), "Soak scroll did not render a decoded preview.");
                await UntilAsync(async () => await store.GetItemAsync(witness) is { MetadataIndexed: true } && !window.BrowseSmokeBusy,
                    "Soak background processing failed to become idle.", 30);
                // Inject only the policy signals. No attempt to suspend the CI host.
                control.SetSuspended(true);
                var resumed = control.CreatePacer().CheckpointAsync(CancellationToken.None);
                Require(!resumed.IsCompleted, "Suspend signal did not hold the next boundary.");
                control.SetSuspended(false);
                await resumed;
                if (cycles % 3 == 0)
                {
                    await window.ToggleBackgroundProcessingAsync();
                    Require(window.GetBackgroundActivity().Paused && window.BrowseSmokeReadersDrained, "Soak pause did not drain real workers.");
                    await window.ToggleBackgroundProcessingAsync();
                    await UntilAsync(() => Task.FromResult(!window.BrowseSmokeBusy), "Soak resume did not settle.", 30);
                }
                Require(reads.All(pair => pair.Value == 1), "Unchanged or corrupt media were fingerprinted repeatedly.");
                foreach (var pair in originalIds)
                    Require((await store.GetItemAsync(pair.Key))?.AssetId == pair.Value, "Soak lost or replaced a catalog identity.");
                await Task.Run(() =>
                {
                    foreach (var pair in fixtures)
                        Require(File.Exists(pair.Key) && new FileInfo(pair.Key).Length == pair.Value.Size &&
                            File.GetLastWriteTimeUtc(pair.Key) == pair.Value.Modified &&
                            pair.Value.Hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(pair.Key))), "Soak changed synthetic originals.");
                });
                using var process = Process.GetCurrentProcess();
                maxMemory = Math.Max(maxMemory, process.PrivateMemorySize64);
                if (cycles == 2) warmedMemory = process.PrivateMemorySize64;
                if (samples.Count == 120) samples.Dequeue();
                samples.Enqueue(new { cycle = cycles, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                    privateBytes = process.PrivateMemorySize64, pending = BackgroundWorkScheduler.Shared.PendingCount,
                    running = BackgroundWorkScheduler.Shared.RunningCount });
                Require(maxGap < 5000, "Soak observed a Dispatcher gap of five seconds or more.");
                Require(warmedMemory == 0 || process.PrivateMemorySize64 <= warmedMemory + 256L * 1024 * 1024,
                    "Soak private memory grew more than 256 MiB after warmup.");
                cycles++;
                // Leave a quiet interval to catch accidental self-restarting passes.
                await Task.Delay(500);
                Require(!window.BrowseSmokeBusy, "Unchanged library failed to remain idle.");
            } while (elapsed.Elapsed.TotalSeconds < seconds || cycles < 3);
            _soakReport = new { passed = true, requestedSeconds = seconds, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                cycles, maxDispatcherGapMs = maxGap, maxPrivateBytes = maxMemory, warmPrivateBytes = warmedMemory,
                originalsChecked = fixtures.Count, originalHashesSizesAndTimesPreserved = true, stableAssetIds = true,
                unchangedFingerprintsReadOnce = true, simulatedSuspendResume = true, allLoadModes = true,
                samples = samples.ToArray(), physicalSleepTested = false, videoDecodeTested = false };
        }
        finally
        {
            pulse.Stop(); control.SetSuspended(false); control.SetPowerState(false); control.Mode = originalMode;
            window.FingerprintReader = originalReader;
            // Owned fixtures are retained for failure diagnostics. Never clean user media.
        }
    }
}
