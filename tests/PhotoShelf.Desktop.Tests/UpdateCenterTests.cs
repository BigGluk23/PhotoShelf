using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoShelf.Application.Updates;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

// Every interaction runs on the real shared WPF dispatcher; fixtures never use a user catalog.
public sealed class UpdateCenterTests
{
    [Fact]
    public Task DisabledStartupPerformsNoCheckAndManualCheckStillWorks() => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(new UpdatePreferences { AutoCheck = false });
        var center = fixture.CreateCenter();
        await center.CheckOnStartupAsync();
        Assert.True(center.IsInitialized);
        Assert.False(center.AutoCheck);
        Assert.Equal(0, fixture.Service.CheckCalls);
        Assert.True(center.CanCheck);

        await center.CheckAsync();
        Assert.Equal(1, fixture.Service.CheckCalls);
        Assert.True(center.HasUpdate);
        Assert.True(center.OfferVisible);
        Assert.True(center.CanDownload);
        Assert.False(center.CanInstall);
        Assert.Equal(0, fixture.Service.DownloadCalls);
        Assert.Empty(fixture.Installed);
        Assert.False((await fixture.Store.LoadAsync()).AutoCheck);
    });

    [Fact]
    public Task DisablingDuringStartupCancelsAndRejectsLateAvailableResult() => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Check = _ => pending.Task; // Model a network implementation that returns after cancellation.
        var center = fixture.CreateCenter();
        await center.InitializeAsync();
        var check = center.CheckOnStartupAsync();
        try
        {
            Assert.True(center.IsChecking);
            await center.SetAutoCheckAsync(false);
            Assert.True(fixture.Service.LastCheckToken.IsCancellationRequested);
            Assert.False((await fixture.Store.LoadAsync()).AutoCheck);
        }
        finally
        {
            pending.TrySetResult(fixture.Available());
            await check;
        }
        Assert.False(center.HasUpdate);
        Assert.False(center.OfferVisible);
        Assert.False(center.IsChecking);
        Assert.True(center.CanCheck);
        Assert.Equal("", center.StatusText);
        Assert.Null((await fixture.Store.LoadAsync()).LastSuccessfulCheck);
        var nextSession = fixture.CreateCenter();
        await nextSession.CheckOnStartupAsync();
        Assert.Equal(1, fixture.Service.CheckCalls);
        Assert.Empty(fixture.Installed);
    });

    [Fact]
    public Task ConcurrentCheckClicksProduceOneRequestAndDoNotDownload() => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Check = _ => pending.Task;
        var center = fixture.CreateCenter();
        await center.InitializeAsync();
        var first = center.CheckAsync();
        try
        {
            await center.CheckAsync();
            await center.CheckOnStartupAsync();
            Assert.Equal(1, fixture.Service.CheckCalls);
            Assert.False(center.CanCheck);
            Assert.True(center.IsChecking);
            Assert.Equal(0, fixture.Service.DownloadCalls);
        }
        finally { pending.TrySetResult(fixture.Available()); await first; }
        Assert.True(center.CanCheck);
        Assert.Empty(fixture.Installed);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task UnavailableOrThrownCheckIsSilentAndPreservesSuccessfulCheckTime(bool automatic, bool throws)
        => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var previous = fixture.Now.AddDays(-2);
        await fixture.Store.SaveAsync(new UpdatePreferences { LastSuccessfulCheck = previous });
        fixture.Service.Check = _ => throws
            ? Task.FromException<UpdateCheckResult>(new IOException("Synthetic network failure: do not show this"))
            : Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.Unavailable));
        var center = fixture.CreateCenter();
        await center.CheckAsync(automatic);
        Assert.Equal("", center.StatusText);
        Assert.False(center.IsChecking);
        Assert.True(center.CanCheck);
        Assert.False(center.HasUpdate);
        Assert.False(center.OfferVisible);
        Assert.Equal(previous, (await fixture.Store.LoadAsync()).LastSuccessfulCheck);
        Assert.Equal(0, fixture.Service.DownloadCalls);
        Assert.Empty(fixture.Installed);
    });

    [Fact]
    public Task FailedManualRecheckDoesNotKeepAStaleLatestVersionClaim() => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Service.Check = _ => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.Current, CheckedAt: fixture.Now));
        var center = fixture.CreateCenter();
        await center.CheckAsync();
        Assert.Contains("последняя версия", center.StatusText);
        fixture.Service.Check = _ => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.Unavailable));
        await center.CheckAsync();
        Assert.Equal("", center.StatusText);
        Assert.Equal(fixture.Now, (await fixture.Store.LoadAsync()).LastSuccessfulCheck);
    });

    [Fact]
    public Task DownloadAndInstallRequireSeparateExplicitActionsAndKeepTheOfferedPackage() => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var center = fixture.CreateCenter();
        await center.CheckOnStartupAsync();
        await center.InstallAsync(); // An offer without a downloaded package cannot install.
        Assert.Equal(0, fixture.Service.DownloadCalls);
        Assert.Empty(fixture.Installed);

        await center.DownloadAsync();
        Assert.Same(fixture.Release, Assert.Single(fixture.Service.RequestedReleases));
        Assert.Equal(1, fixture.Service.CheckCalls); // Download never silently checks for a different release.
        Assert.True(center.CanInstall);
        Assert.False(center.CanDownload);
        Assert.Equal(100d, center.Progress);
        Assert.Empty(fixture.Installed);
        var prepared = Assert.IsType<StagedUpdate>(center.PreparedUpdate);
        Assert.Equal(Path.GetFileName(prepared.StageDirectory), (await fixture.Store.LoadAsync()).PreparedStageId);

        await center.InstallAsync();
        Assert.Same(prepared, Assert.Single(fixture.Installed));
        Assert.Equal(1, fixture.Service.DownloadCalls);
    });

    [Fact]
    public Task CancelledDownloadRejectsLateStageAndNeverPersistsInstallPermission() => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource<StagedUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Download = (_, _, _, _) => pending.Task;
        var center = fixture.CreateCenter();
        await center.CheckAsync();
        var download = center.DownloadAsync();
        try
        {
            Assert.True(center.IsDownloading);
            Assert.False(center.CanInstall);
            center.CancelDownload();
            Assert.True(fixture.Service.LastDownloadToken.IsCancellationRequested);
        }
        finally { pending.TrySetResult(fixture.Descriptor()); await download; }
        Assert.False(center.IsDownloading);
        Assert.False(center.CanInstall);
        Assert.True(center.CanDownload);
        Assert.Null(center.PreparedUpdate);
        Assert.Null((await fixture.Store.LoadAsync()).PreparedStageId);
        await center.InstallAsync();
        Assert.Empty(fixture.Installed);
    });

    [Fact]
    public Task InstallInProgressRejectsASecondClickAndFailureRequiresFreshExplicitConsent() => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var center = fixture.CreateCenter(stage =>
        {
            fixture.Installed.Add(stage);
            return pending.Task;
        });
        await center.CheckAsync();
        await center.DownloadAsync();
        var install = center.InstallAsync();
        try
        {
            Assert.True(center.IsBusy);
            Assert.False(center.CanInstall);
            Assert.False(center.CanCheck);
            await center.InstallAsync();
            Assert.Single(fixture.Installed);
        }
        finally
        {
            pending.TrySetException(new IOException("Synthetic updater launch failed"));
            await install;
        }
        Assert.False(center.IsBusy);
        Assert.True(center.CanInstall);
        Assert.Contains("Обновление отложено", center.StatusText);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Single(fixture.Installed); // No retry, shutdown or implicit next-start install.
    });

    [Fact]
    public Task RestoredSignedStageIsOnlyDisplayStateUntilAnotherInstallClick() => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var stage = fixture.Descriptor();
        Directory.CreateDirectory(stage.StageDirectory);
        await File.WriteAllBytesAsync(stage.ManifestPath, fixture.Release.ManifestBytes.ToArray());
        await File.WriteAllBytesAsync(stage.SignaturePath, fixture.Release.SignatureBytes.ToArray());
        await fixture.Store.SaveAsync(new UpdatePreferences
        { AutoCheck = false, PreparedStageId = Path.GetFileName(stage.StageDirectory) });

        var center = fixture.CreateCenter();
        await center.CheckOnStartupAsync();
        Assert.True(center.CanInstall);
        Assert.False(center.OfferVisible);
        Assert.Equal(0, fixture.Service.CheckCalls);
        Assert.Equal(0, fixture.Service.DownloadCalls);
        Assert.Empty(fixture.Installed);
        center.Dispose(); // Closing a session with a saved stage grants no permission.
        var nextSession = fixture.CreateCenter();
        await nextSession.InitializeAsync();
        Assert.True(nextSession.CanInstall);
        Assert.Empty(fixture.Installed);
        await nextSession.InstallAsync();
        Assert.Equal(stage.StageDirectory, Assert.Single(fixture.Installed).StageDirectory);
    });

    [Fact]
    public Task TamperedRestoredDescriptorDoesNotBecomeAnInstallableOffer() => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var stage = fixture.Descriptor();
        Directory.CreateDirectory(stage.StageDirectory);
        await File.WriteAllBytesAsync(stage.ManifestPath, fixture.Release.ManifestBytes.ToArray());
        await File.WriteAllBytesAsync(stage.SignaturePath, new byte[256]);
        await fixture.Store.SaveAsync(new UpdatePreferences
        { AutoCheck = false, PreparedStageId = Path.GetFileName(stage.StageDirectory) });
        var center = fixture.CreateCenter();
        await center.CheckOnStartupAsync();
        Assert.False(center.CanInstall);
        Assert.False(center.HasUpdate);
        Assert.False(center.OfferVisible);
        await center.InstallAsync();
        Assert.Empty(fixture.Installed);
        Assert.True(File.Exists(stage.ManifestPath)); // Uncertain files are retained, not cleaned as a cache.
        Assert.True(File.Exists(stage.SignaturePath));
    });

    [Fact]
    public Task StartupPrunesOnlyUnreferencedOwnedStagesAndKeepsPreparedDownload() => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var stage = fixture.Descriptor();
        Directory.CreateDirectory(stage.StageDirectory);
        await File.WriteAllBytesAsync(stage.ManifestPath, fixture.Release.ManifestBytes.ToArray());
        await File.WriteAllBytesAsync(stage.SignaturePath, fixture.Release.SignatureBytes.ToArray());
        var orphan = Path.Combine(fixture.StagingRoot, Guid.NewGuid().ToString("N"));
        var unknown = Path.Combine(fixture.StagingRoot, "manual-evidence");
        Directory.CreateDirectory(orphan); Directory.CreateDirectory(unknown);
        await fixture.Store.SaveAsync(new UpdatePreferences
        { AutoCheck = false, PreparedStageId = Path.GetFileName(stage.StageDirectory) });

        var center = fixture.CreateCenter();
        await center.InitializeAsync();

        Assert.True(center.CanInstall);
        Assert.True(Directory.Exists(stage.StageDirectory));
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(unknown));
    });

    [Fact]
    public Task AlreadyInstalledPreparedStageIsRemovedAndPreferenceCleared() => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var stage = fixture.Descriptor();
        Directory.CreateDirectory(stage.StageDirectory);
        await File.WriteAllBytesAsync(stage.ManifestPath, fixture.Release.ManifestBytes.ToArray());
        await File.WriteAllBytesAsync(stage.SignaturePath, fixture.Release.SignatureBytes.ToArray());
        await fixture.Store.SaveAsync(new UpdatePreferences
        { AutoCheck = false, PreparedStageId = Path.GetFileName(stage.StageDirectory) });

        var center = fixture.CreateCenter(currentVersion: fixture.Release.Version);
        await center.InitializeAsync();

        Assert.False(center.CanInstall);
        Assert.False(Directory.Exists(stage.StageDirectory));
        Assert.Null((await fixture.Store.LoadAsync()).PreparedStageId);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SuccessorInitializationPreservesTransportBeforeStartupHealth(bool savedPreference)
        => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var stage = fixture.Descriptor();
        Directory.CreateDirectory(stage.StageDirectory);
        await File.WriteAllBytesAsync(stage.ManifestPath, fixture.Release.ManifestBytes.ToArray());
        await File.WriteAllBytesAsync(stage.SignaturePath, fixture.Release.SignatureBytes.ToArray());
        await File.WriteAllTextAsync(stage.PackagePath, "synthetic package");
        await fixture.Store.SaveAsync(new UpdatePreferences
        { AutoCheck = false, PreparedStageId = savedPreference ? fixture.StageId : null });
        var requestVariable = PhotoShelf.Application.Updates.Installation.UpdateStartupHealth.RequestVariable;
        var installationVariable = PhotoShelf.Application.Updates.Installation.UpdateStartupHealth.InstallationVariable;
        var previousRequest = Environment.GetEnvironmentVariable(requestVariable);
        var previousInstallation = Environment.GetEnvironmentVariable(installationVariable);
        try
        {
            Environment.SetEnvironmentVariable(requestVariable, Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(installationVariable, Guid.NewGuid().ToString("N"));
            var center = fixture.CreateCenter(currentVersion: fixture.Release.Version);
            // Equal versions used to delete the candidate even though startup had not reported ready.
            await center.CheckOnStartupAsync();
            Assert.True(center.IsInitialized);
            Assert.False(center.CanInstall);
            Assert.Equal(0, fixture.Service.CheckCalls);
            Assert.Equal("synthetic package", await File.ReadAllTextAsync(stage.PackagePath));
            Assert.Equal(savedPreference ? fixture.StageId : null, (await fixture.Store.LoadAsync()).PreparedStageId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(requestVariable, previousRequest);
            Environment.SetEnvironmentVariable(installationVariable, previousInstallation);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SkipOrSnoozeSurvivesRestartButManualCheckCanShowTheOffer(bool skipVersion) => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var center = fixture.CreateCenter();
        await center.CheckOnStartupAsync();
        Assert.True(center.OfferVisible);
        await center.DismissAsync(skipVersion);
        Assert.False(center.OfferVisible);
        var preferences = await fixture.Store.LoadAsync();
        if (skipVersion) Assert.Equal(fixture.Release.Version, preferences.SkippedVersion);
        else Assert.Equal(fixture.Now.AddDays(1), preferences.SnoozeUntil);
        center.Dispose();

        var nextSession = fixture.CreateCenter();
        await nextSession.CheckOnStartupAsync();
        Assert.True(nextSession.HasUpdate);
        Assert.False(nextSession.OfferVisible);
        await nextSession.CheckAsync();
        Assert.True(nextSession.OfferVisible);
        Assert.Equal(0, fixture.Service.DownloadCalls);
        Assert.Empty(fixture.Installed);
    });

    [Fact]
    public Task DisposedSessionIgnoresLateNetworkResultWithoutSavingOrInstalling() => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Check = _ => pending.Task;
        var center = fixture.CreateCenter();
        await center.InitializeAsync();
        var check = center.CheckAsync();
        center.Dispose();
        pending.SetResult(fixture.Available());
        await check;
        Assert.True(fixture.Service.LastCheckToken.IsCancellationRequested);
        Assert.False(center.HasUpdate);
        Assert.False(center.CanInstall);
        Assert.False(center.CanCheck);
        Assert.Null((await fixture.Store.LoadAsync()).LastSuccessfulCheck);
        Assert.Empty(fixture.Installed);
    });

    [Fact]
    public Task PanelControlsPersistOptOutAndKeepCheckDownloadAndInstallDistinct() => WpfTestDispatcher.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var center = fixture.CreateCenter();
        var bindingSource = PresentationTraceSources.DataBindingSource;
        var previousTraceLevel = bindingSource.Switch.Level;
        using var bindingErrors = new StringWriter();
        using var bindingListener = new TextWriterTraceListener(bindingErrors);
        bindingSource.Listeners.Add(bindingListener);
        bindingSource.Switch.Level = SourceLevels.Error;
        Window? window = null;
        try
        {
            // Actual BAML parsing, theme resources, StringFormat and binding conversion run in WPF.
            var panel = new UpdatesPanel { DataContext = center };
            window = new Window { Content = panel, Width = 700, Height = 600, ShowInTaskbar = false };
            window.Show();
            await center.InitializeAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Contains(Descendants<TextBlock>(panel), text => text.Text == "Текущая версия: PhotoShelf Ultra 0.10.9");
            var auto = Assert.IsType<CheckBox>(panel.FindName("AutoUpdateCheckBox"));
            var check = Assert.IsType<Button>(panel.FindName("CheckUpdatesButton"));
            Assert.True(auto.IsEnabled);
            Assert.True(auto.IsChecked);
            auto.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            auto.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntilAsync(() => !center.AutoCheck);
            Assert.False((await fixture.Store.LoadAsync()).AutoCheck);
            await center.CheckOnStartupAsync();
            Assert.Equal(0, fixture.Service.CheckCalls);

            var pending = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Service.Check = _ => pending.Task;
            Assert.True(check.IsEnabled);
            check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            try
            {
                await WaitUntilAsync(() => center.IsChecking);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.False(check.IsEnabled);
                Assert.Equal(1, fixture.Service.CheckCalls);
                Assert.Equal(0, fixture.Service.DownloadCalls);
                Assert.Empty(fixture.Installed);
            }
            finally { pending.TrySetResult(fixture.Available()); }
            await WaitUntilAsync(() => center.HasUpdate && center.CanCheck);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var download = FindButton(panel, "Скачать обновление");
            var install = FindButton(panel, "Обновить и перезапустить");
            Assert.True(download.IsEnabled);
            Assert.False(install.IsEnabled);
            Assert.Equal(0, fixture.Service.DownloadCalls);
            download.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntilAsync(() => center.CanInstall);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(install.IsEnabled);
            Assert.False(download.IsEnabled);
            Assert.Empty(fixture.Installed);
            Assert.Same(fixture.Release, Assert.Single(fixture.Service.RequestedReleases));
            install.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntilAsync(() => fixture.Installed.Count == 1);
            Assert.Same(center.PreparedUpdate, Assert.Single(fixture.Installed));
        }
        finally
        {
            window?.Close();
            bindingSource.Listeners.Remove(bindingListener);
            bindingSource.Switch.Level = previousTraceLevel;
        }
        bindingListener.Flush();
        Assert.True(string.IsNullOrWhiteSpace(bindingErrors.ToString()), bindingErrors.ToString());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ShutdownWindowAllowsDeferOnlyBeforeHelperLaunchCommit(bool committed)
        => WpfTestDispatcher.RunAsync(async () =>
    {
        var deferred = 0;
        // Exercise the real internal WPF window without broadening the production public API.
        var type = typeof(UpdateCenter).Assembly.GetType("PhotoShelf.Desktop.UpdateClosingWindow", throwOnError: true)!;
        var window = Assert.IsAssignableFrom<Window>(Activator.CreateInstance(type, (Action)(() => deferred++)));
        try
        {
            window.ShowInTaskbar = false;
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var later = FindButton(window, "Отложить обновление");
            Assert.True(later.IsEnabled);
            if (committed)
            {
                type.GetMethod("CommitLaunch")!.Invoke(window, null);
                Assert.False(later.IsEnabled);
            }
            // A queued click must also obey the consent boundary even if raised after disabling the button.
            later.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(committed ? 0 : 1, deferred);
            window.Close(); // Closing the progress window must not bypass the same boundary.
            Assert.True(window.IsLoaded);
            Assert.Equal(committed ? 0 : 2, deferred);
        }
        finally { type.GetMethod("Finish")!.Invoke(window, null); }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(window.IsLoaded);
    });

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(5), "Expected WPF update state did not settle.");
            await Task.Delay(10);
        }
    }

    private static Button FindButton(DependencyObject root, string content) =>
        Assert.Single(Descendants<Button>(root), button => button.Content?.ToString() == content);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<UpdateCenter> _centers = new();
        public string Root { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            "photoshelf-update-center-test-" + Guid.NewGuid().ToString("N"));
        public string StagingRoot => Path.Combine(Root, "staging");
        public string StageId { get; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset Now { get; } = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        public string PublicKey { get; }
        public VerifiedUpdateRelease Release { get; }
        public UpdatePreferencesStore Store { get; }
        public FakeService Service { get; } = new();
        public List<StagedUpdate> Installed { get; } = new();

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Store = new UpdatePreferencesStore(Path.Combine(Root, "preferences.json"));
            using var rsa = RSA.Create(2048); // Disposable test key; never the release signing key.
            PublicKey = rsa.ExportSubjectPublicKeyInfoPem();
            const string version = "0.10.14";
            var manifest = new UpdateManifest
            {
                ProtocolVersion = 1, Version = version, Runtime = "win-x64",
                PackageUrl = $"https://github.com/BigGluk23/PhotoShelf/releases/download/v{version}-ultra/PhotoShelf-v{version}-ultra-win-x64.zip",
                PackageSha256 = new string('a', 64), PackageManifestSha256 = new string('b', 64),
                PackageBytes = 512, UnpackedBytes = 1024, MinCatalogSchema = 5, MaxCatalogSchema = 5,
                ReleaseNotesUrl = $"https://github.com/BigGluk23/PhotoShelf/releases/tag/v{version}-ultra"
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var signature = rsa.SignData(json, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            Release = UpdateManifestVerifier.Verify(json, signature, PublicKey);
            Service.Check = _ => Task.FromResult(Available());
            Service.Download = (_, _, _, _) => Task.FromResult(Descriptor());
        }

        public UpdateCheckResult Available() => new(UpdateCheckStatus.Available, Release, Now);
        public StagedUpdate Descriptor()
        {
            var directory = Path.Combine(StagingRoot, StageId);
            return new StagedUpdate(directory, Path.Combine(directory, "package.zip"), Path.Combine(directory, "package"),
                Path.Combine(directory, "photoshelf-update.json"), Path.Combine(directory, "photoshelf-update.sig"),
                Release, Array.Empty<UpdatePackageFile>());
        }
        public UpdateCenter CreateCenter(Func<StagedUpdate, Task>? install = null, string currentVersion = "0.10.9")
        {
            var center = new UpdateCenter(Service, Store, StagingRoot, currentVersion, PublicKey,
                install ?? (staged => { Installed.Add(staged); return Task.CompletedTask; }), () => Now);
            _centers.Add(center);
            return center;
        }
        public void Dispose()
        {
            foreach (var center in _centers) center.Dispose();
            Directory.Delete(Root, recursive: true); // Only this fixture's owned synthetic preference/stage files.
        }
    }

    private sealed class FakeService : IUpdateService
    {
        public Func<CancellationToken, Task<UpdateCheckResult>> Check { get; set; } = _ => throw new InvalidOperationException("No check configured.");
        public Func<VerifiedUpdateRelease, string, IProgress<UpdateDownloadProgress>?, CancellationToken, Task<StagedUpdate>> Download { get; set; }
            = (_, _, _, _) => throw new InvalidOperationException("No download configured.");
        public int CheckCalls { get; private set; }
        public int DownloadCalls { get; private set; }
        public CancellationToken LastCheckToken { get; private set; }
        public CancellationToken LastDownloadToken { get; private set; }
        public List<VerifiedUpdateRelease> RequestedReleases { get; } = new();

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            CheckCalls++; LastCheckToken = cancellationToken;
            return Check(cancellationToken);
        }
        public Task<StagedUpdate> DownloadAndStageAsync(VerifiedUpdateRelease release, string updatesDirectory,
            IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            DownloadCalls++; LastDownloadToken = cancellationToken; RequestedReleases.Add(release);
            return Download(release, updatesDirectory, progress, cancellationToken);
        }
    }
}
