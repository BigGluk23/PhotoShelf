using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PhotoShelf.Application.Updates;
using PhotoShelf.Application.Updates.Installation;

namespace PhotoShelf.Desktop;

public partial class MainWindow
{
    private UpdateCenter? _updates;
    private HttpClient? _updateHttp;
    private Window? _updatesWindow;
    private StagedUpdate? _updateToInstall;
    private UpdateClosingWindow? _updateClosingWindow;
    private bool _cancelUpdateInstall;
    private bool _updateHandoffCommitted;
    private GridAnchor? _updateResumeAnchor;
    private readonly UpdateInstallationPaths _updatePaths = new();
    internal static string RunningUpdateVersion => typeof(App).Assembly.GetName().Version is { } version
        ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";

    private UpdateCenter? EnsureUpdateCenter()
    {
        if (LocalCatalogStore.IsIsolatedSmokeCatalog) return null;
        if (_updates is not null) return _updates;
        _updateHttp = UpdateService.CreateHttpClient();
        _updates = new UpdateCenter(new UpdateService(_updateHttp, UpdateTrust.PublicKeyPem, RunningUpdateVersion),
            new UpdatePreferencesStore(Path.Combine(_updatePaths.AppRoot, "updates", "preferences-v1.json")),
            _updatePaths.StagingRoot, RunningUpdateVersion, UpdateTrust.PublicKeyPem, RequestUpdateInstallationAsync,
            requestsRoot: _updatePaths.RequestsRoot, operationsRoot: _updatePaths.OperationsRoot);
        _updates.PropertyChanged += OnUpdateStateChanged;
        return _updates;
    }

    private async Task StartUpdateCheckAsync()
    {
        if (EnsureUpdateCenter() is not { } updates) return;
        try
        {
            // Never join this task to catalog startup. The first frame and library work take priority.
            await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
            await updates.CheckOnStartupAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Background update checks never surface errors or affect catalog startup. */ }
    }

    private void OnUpdateStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closing || _updates is null) return;
        UpdateOfferText.Text = _updates.AvailableText;
        UpdateOfferBanner.Visibility = _updates.OfferVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnUpdatesClicked(object sender, RoutedEventArgs e)
    {
        if (EnsureUpdateCenter() is not { } updates || _closing) return;
        if (_updatesWindow is { IsLoaded: true }) { _updatesWindow.Activate(); return; }
        _updatesWindow = new Window
        {
            Title = "Обновления PhotoShelf Ultra", Width = 660, Height = 470, MinWidth = 530, MinHeight = 320,
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new UpdatesPanel { DataContext = updates, Margin = new Thickness(22) } }
        };
        _updatesWindow.Show();
        try { await updates.InitializeAsync(); } catch (OperationCanceledException) { }
    }

    private async void OnUpdateLaterClicked(object sender, RoutedEventArgs e)
    { if (_updates is { } updates) await updates.DismissAsync(false); }

    private Task RequestUpdateInstallationAsync(StagedUpdate stage)
    {
        if (_closing || !_catalogLoaded || _isCatalogLoading)
            throw new InvalidOperationException("Дождитесь готовности библиотеки.");
        if (_fileOperationActive || !_fileOperationTask.IsCompleted || _hasPendingRecovery)
            throw new InvalidOperationException("Сначала завершите перенос или восстановление файлов в «Операциях».");
        // This callback is reached only by the explicit Install button. A staged archive is never install consent.
        _updateToInstall = stage; _cancelUpdateInstall = false;
        Close();
        return Task.CompletedTask;
    }

    private void ShowUpdateShutdown()
    {
        if (_updateToInstall is null) return;
        _updateClosingWindow = new UpdateClosingWindow(() =>
        {
            _cancelUpdateInstall = true;
            _updateClosingWindow?.SetStage("Обновление отложено. Дожидаюсь безопасного завершения текущего шага…");
        });
        _updateClosingWindow.Show();
    }

    private void SetUpdateShutdownStage(string text) => _updateClosingWindow?.SetStage(text);

    private async Task LaunchConsentedUpdaterAsync()
    {
        if (_updateToInstall is not { } stage) return;
        SetUpdateShutdownStage("Проверяю скачанный комплект. Фото и каталог остаются на своих местах…");
        var verified = await UpdatePackageVerifier.VerifyConsentedStageAsync(stage, UpdateTrust.PublicKeyPem,
            UpdateTrust.CurrentCatalogSchema, CancellationToken.None);
        if (_cancelUpdateInstall) return;
        var helper = Path.Combine(AppContext.BaseDirectory, "PhotoShelf.Updater.exe");
        // Use the helper from the running trusted installation, never execute a download before verification.
        if (!await Task.Run(() => File.Exists(helper) && (File.GetAttributes(helper) & FileAttributes.ReparsePoint) == 0))
            throw new IOException("Компонент обновления отсутствует. Скачайте полный комплект программы с GitHub.");
        var request = await UpdateInstallRequest.CreateFileAsync(verified, _updatePaths, RunningUpdateVersion);
        await SaveUpdateResumeAsync(request, verified.Release.Version);
        if (_cancelUpdateInstall) return; // The unlaunched request is inert and is never automatically replayed.
        SetUpdateShutdownStage("Состояние сохранено. Запускаю обновление после закрытия PhotoShelf…");
        _updateClosingWindow?.CommitLaunch();
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("--install"); start.ArgumentList.Add(request);
        using var process = await Task.Run(() => Process.Start(start)) ?? throw new IOException("Не удалось запустить компонент обновления.");
        SetUpdateShutdownStage("Ожидаю готовности установщика. Защищаю библиотеку от повторного запуска…");
        // Do not release the running app's single-writer mutex merely because Process.Start returned.
        // The helper must first own startup admission, and the parent must explicitly accept handoff.
        // Without that acknowledgement a delayed helper cannot install after an unrelated later close.
        await Task.Run(() => UpdateInstallationHandoff.AcceptWhenReadyAsync(request, _updatePaths,
            process.Id, process.StartTime.ToUniversalTime().Ticks, TimeSpan.FromSeconds(30)));
        // Writers have drained and state is saved. Once the helper consumed the handoff and the
        // parent committed this close, an exceptional close must not resume ordinary catalog work.
        _updateHandoffCommitted = true;
    }

    private async Task SaveUpdateResumeAsync(string requestPath, string targetVersion)
    {
        var anchor = GetGridAnchor();
        var state = new UpdateResumeView(targetVersion, _searchText, _showOnlyMissingCaptureDate,
            anchor.Index, anchor.Path, DateTimeOffset.UtcNow);
        var path = Path.ChangeExtension(requestPath, ".view.json");
        await Task.Run(() =>
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough);
            JsonSerializer.Serialize(output, state); output.Flush(true);
        });
    }

    private async Task RestoreUpdateResumeAsync()
    {
        if (LocalCatalogStore.IsIsolatedSmokeCatalog) return;
        var request = Environment.GetEnvironmentVariable("PHOTOSHELF_UPDATE_REQUEST_ID");
        if (request is null || !Guid.TryParseExact(request, "N", out _)) return;
        try
        {
            var path = Path.Combine(_updatePaths.RequestsRoot, request + ".view.json");
            var state = await Task.Run(() =>
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return input.Length <= 32768 ? JsonSerializer.Deserialize<UpdateResumeView>(input) : null;
            }, _lifetime.Token);
            if (state is null || state.TargetVersion != RunningUpdateVersion || state.CreatedAt < DateTimeOffset.UtcNow.AddDays(-1)
                || state.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(1) || state.AnchorIndex < 0) return;
            _searchText = state.SearchText; SearchBox.Text = state.SearchText;
            _showOnlyMissingCaptureDate = state.MissingCaptureDate;
            _updateResumeAnchor = new(state.AnchorIndex, state.AnchorPath);
        }
        catch (Exception) { /* A view hint never blocks the catalog or changes media/catalog identity. */ }
    }

    private async Task ReportUpdatedStartupReadyAsync()
    {
        if (LocalCatalogStore.IsIsolatedSmokeCatalog) return;
        try { await Task.Run(() => UpdateStartupHealth.ReportReady(Environment.ProcessPath!, _updatePaths)); }
        catch (Exception) { /* A diagnostic receipt cannot turn a usable library into a startup failure. */ }
    }

    private sealed record UpdateResumeView(string TargetVersion, string SearchText, bool MissingCaptureDate,
        long AnchorIndex, string? AnchorPath, DateTimeOffset CreatedAt);
}
