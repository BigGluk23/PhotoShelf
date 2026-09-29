using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Media;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Desktop;

/// <summary>Exercises normal startup and shutdown with one synthetic preview in a newly created catalog.</summary>
internal sealed class UiSmokeSession
{
    private readonly Stopwatch _readyTime = new();
    private string? _fixturePath;
    private bool _closeRequested;
    private bool _closed;
    public bool Ready { get; private set; }
    public bool PreviewRendered { get; private set; }
    public bool NativeDecoderVerified { get; private set; }
    public bool HeifDecoderVerified { get; private set; }
    private HeifInstallationInfo? _heifInstallation;
    public bool ViewerStatusVerified { get; private set; }
    private byte[]? _fixtureHash;
    public double ElapsedReadySeconds { get; private set; }
    public int DispatcherTicks { get; private set; }
    public double MaxDispatcherGapMs { get; private set; }
    public bool GracefulExit => _closeRequested && _closed;

    public async Task SeedCatalogAsync()
    {
        if (!LocalCatalogStore.IsIsolatedSmokeCatalog)
            throw new InvalidOperationException("UI smoke requires its own isolated catalog.");
        var media = Path.Combine(LocalCatalogStore.CatalogDirectory, "smoke-media");
        Directory.CreateDirectory(media);
        NativeDecoderVerified = await Task.Run(() =>
        {
            // Exercise native-library extraction from the actual single-file EXE, not just test/bin.
            var nativeFixture = Path.Combine(media, "native-codec.webp");
            using (var pixels = new SkiaSharp.SKBitmap(2, 2))
            {
                pixels.Erase(SkiaSharp.SKColors.OrangeRed);
                using var image = SkiaSharp.SKImage.FromBitmap(pixels);
                using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Webp, 100)
                    ?? throw new InvalidOperationException("Native WebP encoder is unavailable.");
                using var output = new FileStream(nativeFixture, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                encoded.SaveTo(output);
            }
            var decoded = MediaBitmapLoader.ReadAnimation(nativeFixture, 16, 1);
            if (decoded.Count != 1 || decoded[0].Bitmap.PixelWidth != 2 || decoded[0].Bitmap.PixelHeight != 2)
                throw new InvalidOperationException("Published native WebP decoder failed its synthetic fixture.");
            return true;
        });
        _fixturePath = Path.Combine(media, "preview.heic");
        // Generated coloured quadrants, never a user image. The packaged decoder must render them.
        using (var embedded = typeof(UiSmokeSession).Assembly.GetManifestResourceStream("PhotoShelf.SyntheticHeif.heic")
            ?? throw new InvalidOperationException("Missing synthetic HEIF resource."))
        await using (var output = new FileStream(_fixturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous))
            await embedded.CopyToAsync(output);
        _fixtureHash = SHA256.HashData(await File.ReadAllBytesAsync(_fixturePath));
        _heifInstallation = await Task.Run(() => new HeifDecoderClient().VerifyInstallationAsync());
        if (_heifInstallation.Protocol != "PSH1" || string.IsNullOrWhiteSpace(_heifInstallation.LibheifVersion) ||
            string.IsNullOrWhiteSpace(_heifInstallation.Libde265Version))
            throw new InvalidOperationException("Published HEIF installation did not confirm its bounded protocol and libraries.");
        HeifDecoderVerified = await Task.Run(() =>
        {
            var decoded = MediaBitmapLoader.LoadStillBounded(_fixturePath, 2048);
            if (decoded.PixelWidth != 320 || decoded.PixelHeight != 180 || !decoded.IsFrozen)
                throw new InvalidOperationException("Published HEIF decoder failed its synthetic fixture.");
            return true;
        });
        var file = new FileInfo(_fixturePath);
        var store = new SqliteDesktopCatalogStore();
        await store.InitializeAsync();
        // Maintenance correctly classifies files under TEMP/the catalog as system media.
        // Persist this fixture-only filter before normal startup so a regroup cannot hide the preview.
        await store.SaveAsync(new LocalCatalogState { IncludeSystemFolders = true }, saveItems: false);
        await store.UpsertItemsAsync(new[] { new SavedMediaItem
        {
            Path = _fixturePath, SizeBytes = file.Length, FileModifiedAt = file.LastWriteTime,
            MetadataIndexed = true, CaptureDate = new DateTime(2026, 1, 1)
        } });
    }

    public async Task ObserveAndCloseAsync(MainWindow window)
    {
        if (!LocalCatalogStore.IsIsolatedSmokeCatalog)
            throw new InvalidOperationException("UI smoke requires its own isolated catalog.");
        window.Closed += (_, _) => _closed = true;
        // Prevent accidental interaction with discovery/move controls during this automated fixture.
        window.IsEnabled = false;
        var startupError = await window.InitialCatalogReady.WaitAsync(TimeSpan.FromSeconds(15));
        if (startupError is not null) throw new InvalidOperationException("Initial catalog failed to load.", startupError);
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        if (!window.IsLoaded || !window.IsVisible || window.ActualWidth <= 0 || window.ActualHeight <= 0 || _closed)
            throw new InvalidOperationException("The main window did not become visible and loaded.");
        var previewWait = Stopwatch.StartNew();
        while (_fixturePath is null || !window.HasVisibleSmokePreview(_fixturePath))
        {
            if (previewWait.Elapsed > TimeSpan.FromSeconds(10) || ErrorReporter.ErrorCount != 0 || _closed)
                throw new InvalidOperationException("The synthetic grid preview did not render.");
            await Task.Delay(100);
        }
        PreviewRendered = true;
        await VerifyViewerStatusAsync(window);
        Ready = true;
        _readyTime.Start();
        var previousTick = TimeSpan.Zero;
        while (_readyTime.Elapsed < TimeSpan.FromSeconds(6))
        {
            await Task.Delay(100);
            if (_closed || !window.IsLoaded || ErrorReporter.ErrorCount != 0)
                throw new InvalidOperationException("The UI closed early or reported an error during smoke.");
            var elapsed = _readyTime.Elapsed;
            MaxDispatcherGapMs = Math.Max(MaxDispatcherGapMs, (elapsed - previousTick).TotalMilliseconds);
            previousTick = elapsed;
            DispatcherTicks++;
        }
        ElapsedReadySeconds = _readyTime.Elapsed.TotalSeconds;
        var fixtureBytes = _fixturePath is null ? [] : await File.ReadAllBytesAsync(_fixturePath);
        if (_fixturePath is null || _fixtureHash is null ||
            !_fixtureHash.SequenceEqual(SHA256.HashData(fixtureBytes)))
            throw new InvalidOperationException("The synthetic HEIF original changed during preview.");
        if (DispatcherTicks < 20 || MaxDispatcherGapMs > 2000)
            throw new InvalidOperationException("Dispatcher responsiveness check failed (fewer than 20 observations or a gap over 2 seconds).");
        _closeRequested = true;
        // Calls the production async shutdown path (await writers, save state, close).
        window.Close();
    }

    public object CreateReport(int exitCode) => new
    {
        status = exitCode == 0 && Ready && PreviewRendered && NativeDecoderVerified && HeifDecoderVerified && _heifInstallation is not null && ViewerStatusVerified && GracefulExit && DispatcherTicks >= 20 &&
            MaxDispatcherGapMs <= 2000 && ErrorReporter.ErrorCount == 0 ? "passed" : "failed",
        check = "ui-smoke",
        version = ErrorReporter.Version,
        catalogRoot = LocalCatalogStore.CatalogDirectory,
        logRoot = ErrorReporter.LogRoot,
        ready = Ready,
        previewRendered = PreviewRendered,
        nativeDecoderVerified = NativeDecoderVerified,
        heifDecoderVerified = HeifDecoderVerified,
        heifInstallationVerified = _heifInstallation is not null,
        heifProtocol = _heifInstallation?.Protocol,
        libheifVersion = _heifInstallation?.LibheifVersion,
        libde265Version = _heifInstallation?.Libde265Version,
        viewerStatusVerified = ViewerStatusVerified,
        elapsedReadySeconds = ElapsedReadySeconds,
        dispatcherTicks = DispatcherTicks,
        maxDispatcherGapMs = MaxDispatcherGapMs,
        gracefulExit = GracefulExit,
        errorCount = ErrorReporter.ErrorCount,
        exitCode
    };

    private async Task VerifyViewerStatusAsync(MainWindow owner)
    {
        var viewer = new PhotoViewerWindow(new SqliteDesktopCatalogStore(),
            new CatalogViewQuery { IncludeSystemFolders = true }, 1, 0, _fixturePath)
        { Owner = owner, ShowInTaskbar = false, IsEnabled = false };
        try
        {
            viewer.Show();
            var image = (PreviewImage)viewer.FindName("PhotoImage");
            var status = (System.Windows.Controls.TextBlock)viewer.FindName("PhotoStatusText");
            var wait = Stopwatch.StartNew();
            while (image.Source is null)
            {
                if (wait.Elapsed > TimeSpan.FromSeconds(15) || ErrorReporter.ErrorCount != 0)
                    throw new InvalidOperationException("The synthetic viewer photo did not render.");
                await Task.Delay(50);
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!string.IsNullOrEmpty(status.Text) || status.Visibility != System.Windows.Visibility.Collapsed)
                throw new InvalidOperationException("An empty status overlay obscures the viewer photo.");
            AsyncMediaImage.SetStatus(image, "Synthetic decoder notice");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (status.Visibility != System.Windows.Visibility.Visible || status.Text != "Synthetic decoder notice")
                throw new InvalidOperationException("The viewer hides a nonempty decoder notice.");
            AsyncMediaImage.SetStatus(image, "");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (status.Visibility != System.Windows.Visibility.Collapsed)
                throw new InvalidOperationException("The viewer retains the cleared decoder notice.");
            ViewerStatusVerified = true;
        }
        finally { viewer.Close(); }
    }
}
