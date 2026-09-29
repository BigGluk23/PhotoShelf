using System.IO;
using System.Windows;
using System.Windows.Threading;
using PhotoShelf.Application.Catalog;

namespace PhotoShelf.Desktop;

public partial class MainWindow
{
    internal bool AllowIsolatedLibraryMonitoring { get; set; }
    internal Func<Func<LibraryMonitorBatch, CancellationToken, Task>, LibraryChangeCoordinator>? LibraryMonitorFactory { get; set; }
    private int _monitorCallbacksActive;
    internal bool LibraryMonitorCallbackActive => Volatile.Read(ref _monitorCallbacksActive) > 0;
    private readonly HashSet<string> _watchedFolders = new(StringComparer.OrdinalIgnoreCase);
    private LibraryChangeCoordinator? _libraryMonitor;
    private LibraryCatalogSynchronizer? _librarySynchronizer;
    private Task _monitorConfigurationTask = Task.CompletedTask;
    private string _monitorConfigurationKey = "";
    private string _monitorRulesKey = "";
    private Func<string, bool>? _monitorPathFilter;
    private bool _monitorPaused;
    private bool _metadataRefreshPending;
    private bool _monitorRootsRestored;
    private long _monitorConfigurationRevision;
    private MonitorScope? _monitorScope;
    private readonly Dictionary<string, LibraryRootState> _libraryRootStates = new(StringComparer.OrdinalIgnoreCase);
    private sealed record MonitorScope(FolderInclusionRules Rules, string? BrowseFolder, bool Recursive, bool IncludeSystem, IReadOnlyList<string> LibraryRoots);

    private void QueueLibraryMonitoring()
    {
        if (LocalCatalogStore.IsIsolatedSmokeCatalog && !AllowIsolatedLibraryMonitoring || !_catalogLoaded || _fileOperationActive || _hasPendingRecovery || _closing || _backgroundProcessingPaused) return;
        var revision = ++_monitorConfigurationRevision;
        _monitorConfigurationTask = ConfigureLibraryMonitoringAsync(_monitorConfigurationTask, revision);
    }

    private async Task<Task> ConfigureLibraryMonitoringAsync(Task previous, long revision,
        string[]? requestedRoots = null, CancellationToken requestToken = default, LibraryBrowseTarget? browseRequest = null)
    {
        try
        {
            await previous;
            requestToken.ThrowIfCancellationRequested();
            if (_closing || _fileOperationActive || _hasPendingRecovery || _backgroundProcessingPaused || revision != _monitorConfigurationRevision)
                return requestedRoots is null && browseRequest is null ? Task.CompletedTask : Task.FromCanceled(new CancellationToken(true));
            if (!_monitorRootsRestored)
            {
                foreach (var root in _catalogState.WatchedFolders) RegisterWatchedFolder(root);
                if (_watchedFolders.Count == 0)
                {
                    // Older releases did not persist scan roots. Adopt actual catalog folders,
                    // never every drive displayed in the Explorer-like tree.
                    string? after = null;
                    while (true)
                    {
                        var page = await _desktopCatalogStore.QueryKnownFoldersPageAsync(after, token: _lifetime.Token);
                        if (page.Count == 0) break;
                        foreach (var root in page) RegisterWatchedFolder(root);
                        after = page[^1];
                        if (_watchedFolders.Count > 4096)
                            throw new InvalidOperationException("Для автообновления нужно не более 4096 корневых папок. Выберите общую папку библиотеки и пересканируйте её.");
                    }
                }
                _monitorRootsRestored = true;
                await PersistStateAsync(CaptureState(), _lifetime.Token, 0);
            }
            requestToken.ThrowIfCancellationRequested();
            if (_closing || _fileOperationActive || _backgroundProcessingPaused || revision != _monitorConfigurationRevision)
                return requestedRoots is null && browseRequest is null ? Task.CompletedTask : Task.FromCanceled(new CancellationToken(true));
            var rules = _folderInclusion.Snapshot();
            var browse = _viewMode == LibraryViewMode.Folder ? _activeFolder : null;
            var libraryRoots = LibraryFolderScope.IncludedRoots(_watchedFolders, rules);
            var reconciliationTargets = requestedRoots is null ? null : LibraryFolderScope.ReconciliationTargets(requestedRoots, libraryRoots)
                .Where(path => rules.MayContainIncluded(path) && !PhotoScanner.IsIgnoredPath(path, _includeSystemFolders) &&
                    !path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                        .Any(part => part.StartsWith(".photoshelf-", StringComparison.OrdinalIgnoreCase))).ToArray();
            var roots = libraryRoots.ToList();
            // A recursive library watcher already covers this location. Foreground
            // browse has its own exact depth and does not require a second watcher.
            if (browse is not null && !libraryRoots.Any(root => LibraryCatalogSynchronizer.IsUnder(browse, root))) roots.Add(browse);
            roots = roots.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var rulesKey = _includeSystemFolders + "|" + string.Join('\n', rules.IncludedFolders) + "|" + string.Join('\n', rules.ExcludedFolders);
            var key = string.Join('\n', roots) + "|library:" + string.Join('\n', libraryRoots) + "|" + browse + "|" + _includeSubfolders + "|" + rulesKey;
            if (_libraryMonitor is null)
            {
                _librarySynchronizer = new(_desktopCatalogStore, PublishLibraryUpdateAsync);
                async Task ProcessBatchAsync(LibraryMonitorBatch batch, CancellationToken token)
                {
                    Interlocked.Increment(ref _monitorCallbacksActive);
                    try
                    {
                        var scope = _monitorScope;
                        if (scope is not null) await _librarySynchronizer.ProcessAsync(batch, scope.Rules,
                            scope.BrowseFolder, scope.Recursive, scope.IncludeSystem, token, scope.LibraryRoots);
                    }
                    finally { Interlocked.Decrement(ref _monitorCallbacksActive); }
                }
                _libraryMonitor = LibraryMonitorFactory?.Invoke(ProcessBatchAsync) ?? new LibraryChangeCoordinator(ProcessBatchAsync);
                if (_searchStopped)
                {
                    await _libraryMonitor.PauseAsync();
                    _monitorPaused = true;
                }
            }
            Task reconciliation = Task.CompletedTask;
            if (_monitorConfigurationKey != key)
            {
                // Publish the new consumer scope only after the old callback has
                // really returned, including committed-result publication.
                await _libraryMonitor.PauseAsync();
                _monitorPaused = true;
                requestToken.ThrowIfCancellationRequested();
                if (_closing || _fileOperationActive || _hasPendingRecovery || _backgroundProcessingPaused || revision != _monitorConfigurationRevision)
                    return requestedRoots is null && browseRequest is null ? Task.CompletedTask : Task.FromCanceled(new CancellationToken(true));
                _monitorScope = new(rules, browse, _includeSubfolders, _includeSystemFolders, libraryRoots);
                _monitorPathFilter ??= path => _monitorScope is { } scope && !PhotoScanner.IsIgnoredPath(path, scope.IncludeSystem) &&
                    (scope.LibraryRoots.Any(root => LibraryCatalogSynchronizer.IsUnder(path, root)) && scope.Rules.MayContainIncluded(path) ||
                     scope.BrowseFolder is not null && LibraryFolderScope.Contains(path, scope.BrowseFolder, scope.Recursive));
                foreach (var removed in _libraryRootStates.Keys.Except(roots, StringComparer.OrdinalIgnoreCase).ToArray()) _libraryRootStates.Remove(removed);
                var configuration = new LibraryMonitorConfiguration(roots,
                    CatalogStoragePaths.InternalRoots,
                    ShouldObservePath: _monitorPathFilter, ShouldObserveFilePath: PhotoItem.IsSupported,
                    NonRecursiveRoots: roots.Where(root => !_includeSubfolders &&
                        !libraryRoots.Any(parent => LibraryCatalogSynchronizer.IsUnder(root, parent))).ToArray());
                if (_monitorRulesKey.Length > 0 && _monitorRulesKey != rulesKey) _libraryMonitor.RequestReconciliation();
                if (browseRequest is not null) reconciliation = (await _libraryMonitor.ConfigureAndBrowseAsync(configuration, browseRequest, requestToken)).Completion;
                else if (requestedRoots is null) await _libraryMonitor.ConfigureAsync(configuration);
                else reconciliation = (await _libraryMonitor.ConfigureAndReconcileAsync(configuration, reconciliationTargets!, requestToken)).Completion;
                _monitorRulesKey = rulesKey;
                _monitorConfigurationKey = key;
            }
            else if (browseRequest is not null) reconciliation = _libraryMonitor.BrowseAsync(browseRequest, requestToken);
            else if (requestedRoots is not null) reconciliation = _libraryMonitor.ReconcileAsync(reconciliationTargets!, requestToken);
            if (_monitorPaused && !_backgroundProcessingPaused && !_searchStopped)
            {
                await _libraryMonitor.ResumeAsync(reconcileAllRoots: requestedRoots is null && browseRequest is null);
                _monitorPaused = false;
            }
            LibraryStatusText.Text = _backgroundProcessingPaused ? "Автообновление: пауза" :
                _searchStopped ? "Поиск остановлен · автообновление: пауза" : roots.Count == 0 ? "Автообновление: добавьте папку" : "Автообновление включено";
            return reconciliation;
        }
        catch (OperationCanceledException) { return requestedRoots is null && browseRequest is null ? Task.CompletedTask : Task.FromCanceled(new CancellationToken(true)); }
        catch (Exception exception)
        {
            LibraryStatusText.Text = $"Автообновление: {exception.Message}";
            return requestedRoots is null && browseRequest is null ? Task.CompletedTask : Task.FromException(exception);
        }
    }

    private void RegisterWatchedFolder(string path)
    {
        var root = Path.GetFullPath(path);
        if (_watchedFolders.Any(parent => LibraryCatalogSynchronizer.IsUnder(root, parent))) return;
        _watchedFolders.RemoveWhere(child => LibraryCatalogSynchronizer.IsUnder(child, root));
        _watchedFolders.Add(root);
    }

    private async Task PublishLibraryUpdateAsync(LibraryCatalogUpdate update)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            if (_closing || _lifetime.IsCancellationRequested) return;
            foreach (var state in update.Roots)
            {
                if (_monitorScope is { } scope && !scope.LibraryRoots.Contains(state.Path, StringComparer.OrdinalIgnoreCase) &&
                    !string.Equals(scope.BrowseFolder, state.Path, StringComparison.OrdinalIgnoreCase)) continue;
                _libraryRootStates[state.Path] = state;
                if (_folderNodes.TryGetValue(NormalizePath(state.Path), out var node)) node.AvailabilityText = RootAvailabilityText(state);
            }
            var unavailable = _libraryRootStates.Values.Where(state => state.Availability != FileAvailability.Available).ToArray();
            LibraryStatusText.Text = _searchStopping ? "Поиск: останавливаю…" :
                _searchStopped ? (_browseCancellation is { IsCancellationRequested: false }
                    ? "Читаю выбранную папку · автообновление: пауза" : "Поиск остановлен · автообновление: пауза") :
                unavailable.Length == 0 ? "Автообновление включено" :
                $"Автообновление: {unavailable.Length} папок требуют внимания";
            LibraryStatusText.ToolTip = string.Join("\n", unavailable.Select(state => $"{state.Path}: {RootAvailabilityText(state)}"));
            var retained = (PhotoRows as VirtualPhotoRows)?.LoadedItems.ToArray() ?? [];
            foreach (var rename in update.Renames)
            {
                var selected = _selection.Find(rename.Source);
                if (selected is not null) { _selection.Remove(selected); selected.ApplyCatalogObservation(rename.Destination); _selection.Add(selected); }
                foreach (var item in retained.Where(item => item.Path.Equals(rename.Source, StringComparison.OrdinalIgnoreCase)))
                    item.ApplyCatalogObservation(rename.Destination);
                if (_selectedPhoto?.Path.Equals(rename.Source, StringComparison.OrdinalIgnoreCase) == true)
                    _selectedPhoto.ApplyCatalogObservation(rename.Destination);
            }
            foreach (var saved in update.Items)
            {
                _selection.Find(saved.Path)?.ApplyCatalogObservation(saved);
                foreach (var item in retained.Where(item => item.Path.Equals(saved.Path, StringComparison.OrdinalIgnoreCase))) item.ApplyCatalogObservation(saved);
            }
            if (update.Items.Count > 0)
            {
                foreach (var viewer in OwnedWindows.OfType<PhotoViewerWindow>()) viewer.CatalogItemsChanged(update.Items, update.Renames);
                if (IsCurrentViewAffected(update)) NoteCatalogChanged();
                if (_selectedPhoto is { } selected && update.Items.Any(item => item.Path.Equals(selected.Path, StringComparison.OrdinalIgnoreCase))) UpdateInfoPanel();
            }
            if (update.Completed && update.CatalogChanged)
            {
                CompleteCatalogStage();
                if (!_fileOperationActive && !_hasPendingRecovery)
                {
                    if (_metadataTask.IsCompleted) { _metadataRefreshPending = false; StartMetadataIndexing(false); }
                    else _metadataRefreshPending = true;
                }
            }
        }, DispatcherPriority.Background);
        if (update.Completed && update.CatalogChanged && !_closing)
        {
            var count = await _desktopCatalogStore.CountAsync(new CatalogViewQuery { IncludeSystemFolders = true }, _lifetime.Token);
            await Dispatcher.InvokeAsync(() => _catalogCount = count, DispatcherPriority.Background);
        }
    }

    private bool IsCurrentViewAffected(LibraryCatalogUpdate update)
    {
        bool IncludesPath(string path) => _viewMode == LibraryViewMode.Folder && _activeFolder is not null
            ? LibraryFolderScope.Contains(path, _activeFolder, _includeSubfolders)
            : _folderInclusion.IsIncluded(path);
        // Include a rename's former location too, so moving out of the view removes its tile.
        return update.Items.Any(item => IncludesPath(item.Path)) || update.Renames.Any(rename => IncludesPath(rename.Source));
    }

    private static string RootAvailabilityText(LibraryRootState state) => state.Availability switch
    {
        FileAvailability.RootOffline => "Диск отключён",
        FileAvailability.Missing => "Папка отсутствует",
        FileAvailability.AccessDenied => "Нет доступа",
        FileAvailability.NeedsVerification => state.ErrorCode is "WatcherLimitPeriodicFallback" or "WatcherFailedPeriodicFallback" ? "Периодическая сверка" : "Нужна повторная проверка",
        _ => ""
    };
}
