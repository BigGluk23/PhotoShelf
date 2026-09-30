using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using PhotoShelf.Application.Updates;

namespace PhotoShelf.Desktop;

/// <summary>One UI session. Checking, downloading and consent to install are separate transitions.</summary>
public sealed class UpdateCenter : INotifyPropertyChanged, IDisposable
{
    private readonly IUpdateService _service;
    private readonly UpdatePreferencesStore _store;
    private readonly string _stagingRoot;
    private readonly Func<StagedUpdate, Task> _install;
    private readonly string _publicKey;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private UpdatePreferences _preferences = new();
    private Task? _initialization;
    private CancellationTokenSource? _checkCancellation, _downloadCancellation;
    private bool _checkAutomatic, _disposed, _initialized, _checking, _downloading, _installing, _offer;
    private long _checkRevision;
    private string _status = "";
    private double _progress;
    private VerifiedUpdateRelease? _available;
    private StagedUpdate? _staged;

    public UpdateCenter(IUpdateService service, UpdatePreferencesStore store, string stagingRoot,
        string currentVersion, string publicKey, Func<StagedUpdate, Task> install, Func<DateTimeOffset>? clock = null)
    {
        _service = service; _store = store; _stagingRoot = stagingRoot;
        CurrentVersion = currentVersion; _publicKey = publicKey; _install = install;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string CurrentVersion { get; }
    public string CurrentVersionText => DisplayVersion(CurrentVersion);
    public bool AutoCheck => _preferences.AutoCheck;
    public bool IsChecking => _checking;
    public bool IsDownloading => _downloading;
    public bool IsBusy => _checking || _downloading || _installing;
    public bool IsInitialized => _initialized;
    public bool CanCheck => _initialized && !IsBusy && !_disposed;
    public bool CanDownload => _available is not null && _staged is null && !IsBusy && !_disposed;
    public bool CanInstall => _staged is not null && !IsBusy && !_disposed;
    public bool HasUpdate => _available is not null;
    public bool OfferVisible => _offer && HasUpdate;
    public string AvailableText => _available is null ? "" : $"Доступна PhotoShelf Ultra {DisplayVersion(_available.Version)}";
    public string DownloadSizeText => _available is null ? "" : $"Размер загрузки: {_available.Manifest.PackageBytes / 1048576d:N1} МБ";
    public string LastCheckedText => _preferences.LastSuccessfulCheck is { } date
        ? $"Последняя успешная проверка: {date.ToLocalTime():g}" : "Последняя успешная проверка: —";
    public string StatusText => _checking ? "Проверяю наличие обновлений…" : _status;
    public double Progress => _progress;
    public string? ReleaseNotesUrl => _available?.Manifest.ReleaseNotesUrl;
    public StagedUpdate? PreparedUpdate => _staged;

    private static string DisplayVersion(string value) =>
        UpdateVersion.TryParse(value, out var version) && version.Build == 0
            ? $"{version.Major}.{version.Minor}" : value;

    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        try { _preferences = await _store.LoadAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception) { _preferences = new UpdatePreferences { AutoCheck = false }; }
        if (_disposed) return;
        if (_preferences.PreparedStageId is { } id && Guid.TryParseExact(id, "N", out _))
        {
            try
            {
                var staged = await UpdatePackageVerifier.ReadStagedDescriptorAsync(Path.Combine(_stagingRoot, id),
                    _publicKey, UpdateTrust.CurrentCatalogSchema, _lifetime.Token);
                if (UpdateVersion.IsNewer(staged.Release.Version, CurrentVersion))
                {
                    _staged = staged; _available = staged.Release;
                    _status = "Загрузка сохранена. Перед установкой пакет будет проверен ещё раз.";
                    // Restoring downloaded state never grants permission to install or notify on startup.
                }
            }
            catch (Exception) { /* Retain uncertain staged files; never activate or treat them as trusted. */ }
        }
        _initialized = true; Changed();
    }

    public async Task CheckOnStartupAsync()
    {
        await InitializeAsync();
        if (AutoCheck && !_disposed) await CheckAsync(automatic: true);
    }

    public async Task SetAutoCheckAsync(bool enabled)
    {
        await InitializeAsync();
        if (_disposed) return;
        _preferences = _preferences with { AutoCheck = enabled };
        if (!enabled)
        {
            _offer = false;
            if (_checkAutomatic) { _checkRevision++; _checkCancellation?.Cancel(); }
        }
        Changed();
        try { await SavePreferencesAsync(); }
        catch (Exception) { _status = "Настройку не удалось сохранить. Проверьте доступ к служебной папке."; Changed(); }
    }

    public async Task CheckAsync(bool automatic = false)
    {
        await InitializeAsync();
        if (!CanCheck || automatic && !AutoCheck) return;
        _checking = true; _checkAutomatic = automatic;
        if (!automatic) _status = "";
        var revision = ++_checkRevision;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _checkCancellation = cancellation;
        Changed();
        try
        {
            var result = await _service.CheckAsync(cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || revision != _checkRevision || automatic && !AutoCheck) return;
            if (result.Status == UpdateCheckStatus.Unavailable) return;
            _preferences = _preferences with { LastSuccessfulCheck = result.CheckedAt ?? _clock() };
            if (result.Status == UpdateCheckStatus.Available && result.Release is { } release)
            {
                if (_staged?.Release.Manifest.PackageSha256 != release.Manifest.PackageSha256) _staged = null;
                _available = release;
                _offer = !automatic || _preferences.ShouldSuggest(release, _clock());
                if (_staged is null) _status = "Обновление загрузится только после нажатия «Скачать обновление».";
            }
            else
            {
                _available = null; _staged = null; _offer = false;
                _status = automatic ? "" : "Установлена последняя версия.";
            }
            try { await SavePreferencesAsync(); } catch (Exception) { /* A check must never produce an error notification. */ }
        }
        catch (Exception) { /* Network/check failures are silent, including a manual check. */ }
        finally
        {
            if (ReferenceEquals(_checkCancellation, cancellation)) _checkCancellation = null;
            _checking = false; _checkAutomatic = false; Changed();
        }
    }

    public async Task DownloadAsync()
    {
        if (!CanDownload || _available is not { } release) return;
        _downloading = true; _progress = 0; _status = "Подготавливаю загрузку…";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _downloadCancellation = cancellation; Changed();
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                if (_disposed || !ReferenceEquals(_downloadCancellation, cancellation)) return;
                _progress = value.TotalBytes > 0 ? Math.Clamp(100d * value.BytesReceived / value.TotalBytes, 0, 100) : 0;
                _status = value.Stage; Changed();
            });
            var staged = await _service.DownloadAndStageAsync(release, _stagingRoot, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _staged = staged; _progress = 100;
            _preferences = _preferences with { PreparedStageId = Path.GetFileName(staged.StageDirectory) };
            _status = "Обновление скачано и проверено. Установка — только по кнопке «Обновить и перезапустить».";
            try { await SavePreferencesAsync(); }
            catch (Exception) { _status += " Состояние загрузки не удалось сохранить для следующего запуска."; }
        }
        catch (OperationCanceledException) { _status = "Загрузка отменена. Текущая версия продолжает работать."; }
        catch (Exception) { _status = "Загрузка или проверка пакета не завершена. Можно повторить загрузку; текущая версия сохранена."; }
        finally
        {
            if (ReferenceEquals(_downloadCancellation, cancellation)) _downloadCancellation = null;
            _downloading = false; Changed();
        }
    }

    public void CancelDownload() => _downloadCancellation?.Cancel();

    public async Task InstallAsync()
    {
        if (!CanInstall || _staged is not { } staged) return;
        _installing = true; _status = "Подготавливаю безопасный перезапуск…"; Changed();
        try { await _install(staged); }
        catch (Exception error) { _status = "Обновление отложено: " + error.Message; }
        finally { _installing = false; Changed(); }
    }

    public async Task DismissAsync(bool skipVersion)
    {
        if (_available is null) return;
        _preferences = skipVersion
            ? _preferences with { SkippedVersion = _available.Version, SnoozeUntil = null }
            : _preferences with { SnoozeUntil = _clock().AddDays(1) };
        _offer = false; Changed();
        try { await SavePreferencesAsync(); }
        catch (Exception) { _status = "Не удалось сохранить отсрочку предложения обновиться."; Changed(); }
    }

    private async Task SavePreferencesAsync()
    {
        await _saveGate.WaitAsync(_lifetime.Token);
        try { await _store.SaveAsync(_preferences, _lifetime.Token); }
        finally { _saveGate.Release(); }
    }

    private void Changed([CallerMemberName] string? _ = null)
    { if (!_disposed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _checkCancellation?.Cancel(); _downloadCancellation?.Cancel();
        // In-flight I/O owns its CTS and releases it on completion; cancellation never grants install consent.
    }
}
