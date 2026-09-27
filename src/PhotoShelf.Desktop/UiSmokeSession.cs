using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using PhotoShelf.Application.Catalog;
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
        _fixturePath = Path.Combine(media, "preview.png");
        // An embedded application illustration is the only media fixture; never discover user files.
        using (var embedded = AppResources.Open(AppResources.GiraffeUri))
        await using (var output = new FileStream(_fixturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous))
            await embedded.CopyToAsync(output);
        var file = new FileInfo(_fixturePath);
        var store = new SqliteDesktopCatalogStore();
        await store.InitializeAsync();
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
        if (DispatcherTicks < 20 || MaxDispatcherGapMs > 2000)
            throw new InvalidOperationException("Dispatcher responsiveness check failed (fewer than 20 observations or a gap over 2 seconds).");
        _closeRequested = true;
        // Calls the production async shutdown path (await writers, save state, close).
        window.Close();
    }

    public object CreateReport(int exitCode) => new
    {
        status = exitCode == 0 && Ready && PreviewRendered && GracefulExit && DispatcherTicks >= 20 &&
            MaxDispatcherGapMs <= 2000 && ErrorReporter.ErrorCount == 0 ? "passed" : "failed",
        check = "ui-smoke",
        version = ErrorReporter.Version,
        catalogRoot = LocalCatalogStore.CatalogDirectory,
        logRoot = ErrorReporter.LogRoot,
        ready = Ready,
        previewRendered = PreviewRendered,
        elapsedReadySeconds = ElapsedReadySeconds,
        dispatcherTicks = DispatcherTicks,
        maxDispatcherGapMs = MaxDispatcherGapMs,
        gracefulExit = GracefulExit,
        errorCount = ErrorReporter.ErrorCount,
        exitCode
    };
}
