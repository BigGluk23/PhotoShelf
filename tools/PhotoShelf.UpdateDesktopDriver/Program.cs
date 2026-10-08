using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Application.Files;
using PhotoShelf.Application.Metadata;
using PhotoShelf.Application.Updates;
using PhotoShelf.Application.Updates.Installation;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.UpdateDesktopDriver;

/// <summary>
/// Windows-only external UI Automation driver. The applications run their normal entrypoint and
/// their normal profile paths; only an atomically claimed, initially absent CI profile is allowed.
/// No production executable accepts a test trust key, profile override or auto-install argument.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly Dictionary<string, bool> Assertions = new();
    private static readonly List<Process> OwnedProcesses = [];
    private static readonly Dictionary<string, (string Hash, long Length, long Modified)> Originals = new(StringComparer.OrdinalIgnoreCase);
    private static string _phase = "initializing", _runRoot = "", _profile = "", _ownerId = "";
    private static string _controlRoot = "", _controlOwner = "";
    private static readonly List<ProcessRecord> ProcessEvidence = [];
    private static readonly List<RaceRecord> RaceEvidence = [];
    private sealed record ProcessRecord(int Id, DateTime StartedUtc, string Executable);
    private sealed record GateRecord(string Checkpoint, int ProcessId, string Version, DateTimeOffset AtUtc);
    private sealed record RaceRecord(string Checkpoint, int ProcessId, bool BusyPromptShown, int ExitCode, bool CatalogStartup);

    [MTAThread]
    private static int Main(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 4) return 2;
        var reportPath = Path.Combine(Path.GetFullPath(args[1]), "updater-desktop-e2e.json");
        var status = "failed";
        string? error = null;
        object? failureDiagnostics = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            Require(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
                Environment.GetEnvironmentVariable("RUNNER_OS") == "Windows", "Only a disposable Windows Actions runner is permitted.");
            _runRoot = Path.GetFullPath(args[0]);
            Require(_runRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(_runRoot).StartsWith("PhotoShelf-WpfUpdateLifecycle-", StringComparison.Ordinal), "Unowned test source directory.");
            UpdatePackageVerifier.RejectReparseAncestors(_runRoot);
            _controlRoot = Path.Combine(_runRoot, "lifecycle-control");
            _controlOwner = await File.ReadAllTextAsync(Path.Combine(_controlRoot, "owned-test-control.txt"));
            Require(Guid.TryParseExact(_controlOwner, "N", out _), "Missing owned compile-time scheduling control.");
            Require(args[2].Length == 40 && args[2].All(Uri.IsHexDigit), "Invalid source revision.");
            var oldExe = Path.Combine(_runRoot, "app-1.11.1", "PhotoShelf.exe");
            var oldHelper = Path.Combine(_runRoot, "app-1.11.1", "PhotoShelf.Updater.exe");
            var newPackage = Path.Combine(_runRoot, "app-1.11.2");
            RequireVersion(oldExe, 1); RequireVersion(oldHelper, 1);
            RequireVersion(Path.Combine(newPackage, "PhotoShelf.exe"), 2);
            RequireVersion(Path.Combine(newPackage, "PhotoShelf.Updater.exe"), 2);
            var key = await File.ReadAllTextAsync(Path.Combine(_runRoot, "source", "src", "PhotoShelf.Application", "Updates", "TrustedUpdateKey.pem"));
            var fallbackErrors = Path.Combine(Path.GetTempPath(), "PhotoShelf", "diagnostics", "errors");
            var fallbackBefore = ErrorSnapshot(fallbackErrors);
            ClaimEmptyProfile();
            var media = await SeedAsync(newPackage, key);
            _phase = "release-seed-catalog-handles";
            // Disposed provider connections stay open in this driver's default pool. The normal
            // application's migration must acquire its source independently of the seed writer.
            SqliteConnection.ClearAllPools();
            using (var probe = new FileStream(Path.Combine(_profile, "catalog-v2.sqlite"), FileMode.Open,
                FileAccess.Read, FileShare.None))
                Require(probe.Length > 0, "Seed catalog is empty.");
            Assertions["seedCatalogHandlesReleased"] = true;
            _phase = "normal-old-entrypoint";
            var old = StartOwned(oldExe);
            AutomationElement? oldWindow = null;
            await UntilAsync(() => (oldWindow = MainWindow(old.Id)) is not null && HasText(oldWindow, "preserved-photo.png"), "Old application did not show synthetic catalog.");
            RequireGateRecord("catalog-entry-" + old.Id + ".json", "catalog-entry", old.Id, "1.11.1");
            Assertions["normalOldEntrypoint"] = true;
            var catalog = new CatalogLocation().Discover().Selection?.DirectoryPath ?? throw new IOException("No real catalog generation was selected.");
            var rowsBefore = await RowsSnapshotAsync(catalog);
            var journalsBefore = JournalSnapshot(catalog);
            Require(journalsBefore.Length == 1, "Synthetic completed move journal was not migrated.");
            var search = FindById(oldWindow!, "SearchBox");
            ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).SetValue("preserved-photo");
            await UntilAsync(() => HasText(oldWindow!, "preserved-photo.png") && !HasText(oldWindow!, "other-photo.png"), "Search did not settle before update.");
            _phase = "open-old-settings";
            InvokeButton(oldWindow!, "Настройки");
            AutomationElement? settings = null;
            await UntilAsync(() => (settings = FindWindow(old.Id, "Настройки PhotoShelf")) is not null,
                "Owned Settings window did not open after its real button was invoked.");
            _phase = "wait-prepared-update-offer";
            await UntilAsync(() => Button(settings!, "Обновить и перезапустить") is { Current.IsEnabled: true },
                "Prepared signed release was not offered for explicit installation.");
            var automatic = FindById(settings!, "AutoUpdateCheckBox");
            Require(((TogglePattern)automatic.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.Off,
                "Automatic network checking was unexpectedly enabled.");
            Require(!Directory.Exists(new UpdateInstallationPaths().RequestsRoot), "Prepared state authorized installation before a click.");
            Assertions["preparedStateDoesNotInstall"] = true;
            _phase = "explicit-install-button";
            InvokeButton(settings!, "Обновить и перезапустить");
            Assertions["realInstallButton"] = true;
            var paths = new UpdateInstallationPaths();
            Process? helper = null;
            ActiveInstallationTarget? active = null;
            _phase = "repeated-launch-before-pointer";
            // The pause is compiled into the owned source copy at an existing installer checkpoint.
            // Ordinary launches still use the real zero-argument production entrypoint and controls.
            await UntilAsync(() =>
            {
                helper ??= FindOwnedProcess(oldHelper);
                return old.HasExited && helper is not null && GateReached("before-pointer-publish");
            }, "WPF close did not reach its real helper's before-pointer checkpoint.", 150);
            Require(old.ExitCode == 0, "Old application did not close gracefully.");
            Assertions["oldProcessExited"] = true;
            Assertions["helperLaunched"] = true;
            RequireGateRecord("before-pointer-publish.reached.json", "before-pointer-publish", helper!.Id, "1.11.1");
            Require(!File.Exists(paths.ActivePointerPath), "The fresh profile already has an active pointer before publication.");
            await AssertRepeatedLaunchBlockedAsync(oldExe, "before-pointer-publish", catalog, rowsBefore, journalsBefore);
            Assertions["repeatedLaunchBeforePointerBlocked"] = true;
            ReleaseGate("before-pointer-publish");

            _phase = "repeated-launch-after-pointer";
            await UntilAsync(() => GateReached("pointer-published"), "Helper did not reach its published-pointer checkpoint.", 30);
            RequireGateRecord("pointer-published.reached.json", "pointer-published", helper.Id, "1.11.1");
            active = ActiveInstallationResolver.Resolve(paths, key);
            Require(active?.Version == "1.11.2", "The checkpoint does not point to the signed new version.");
            Require(!HasReadyReceipt(paths), "New-version health was reported before the helper launched it.");
            await AssertRepeatedLaunchBlockedAsync(oldExe, "pointer-published", catalog, rowsBefore, journalsBefore);
            Assertions["repeatedLaunchAfterPointerBlocked"] = true;
            ReleaseGate("pointer-published");

            _phase = "repeated-launch-before-health";
            Process? successor = null;
            AutomationElement? newWindow = null;
            await UntilAsync(() =>
            {
                successor ??= FindOwnedProcess(active!.ExecutablePath);
                return successor is not null && GateReached("candidate-before-health") &&
                    (newWindow = MainWindow(successor.Id)) is not null && HasText(newWindow, "preserved-photo.png");
            }, "New actual WPF application did not show its catalog before startup health.", 30);
            RequireGateRecord("candidate-before-health.reached.json", "candidate-before-health", successor!.Id, "1.11.2");
            Require(!HasReadyReceipt(paths), "The candidate wrote health before its checkpoint was released.");
            var requestPath = Directory.GetFiles(paths.RequestsRoot, "*.json", SearchOption.TopDirectoryOnly)
                .Single(path => !path.EndsWith(".view.json", StringComparison.Ordinal));
            var viewPath = Path.ChangeExtension(requestPath, ".view.json");
            string requestId;
            string stageDirectory;
            using (var request = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath)))
            {
                requestId = request.RootElement.GetProperty("requestId").GetString()!;
                stageDirectory = request.RootElement.GetProperty("stageDirectory").GetString()!;
                Require(request.RootElement.GetProperty("parentProcessId").GetInt32() == old.Id &&
                    request.RootElement.GetProperty("expectedVersion").GetString() == "1.11.2",
                    "Install request lost parent/version identity before startup health.");
            }
            await AssertRepeatedLaunchBlockedAsync(oldExe, "candidate-before-health", catalog, rowsBefore, journalsBefore);
            Assertions["repeatedLaunchBeforeHealthBlocked"] = true;
            ReleaseGate("candidate-before-health");

            _phase = "new-application-ready";
            string? receiptPath = null;
            await UntilAsync(() =>
            {
                receiptPath = Directory.GetFiles(paths.OperationsRoot, "startup-ready.json", SearchOption.AllDirectories).SingleOrDefault();
                return receiptPath is not null;
            }, "Actual new application's first projection never wrote its startup-ready receipt.");
            var receipt = JsonSerializer.Deserialize<UpdateStartupHealthRecord>(await File.ReadAllTextAsync(receiptPath!), Json)!;
            Require(receipt.State == "ready" && receipt.InstallationId == active!.InstallationId &&
                receipt.RequestId == requestId && Guid.TryParseExact(receipt.RequestId, "N", out _),
                "Receipt is not tied to the activated successor.");
            Require(!File.Exists(requestPath), "A confirmed startup retained its consumed one-time request.");
            Require(!File.Exists(viewPath), "A confirmed startup retained its one-time view-resume hint.");
            Require(!Directory.Exists(stageDirectory), "A confirmed startup retained its verified staging directory.");
            Assertions["confirmedTransportCleaned"] = true;
            Assertions["startupReadyReceipt"] = true;
            RequireVersion(active!.ExecutablePath, 2);
            Assertions["newProcessLaunched"] = true;
            Require(((ValuePattern)FindById(newWindow!, "SearchBox").GetCurrentPattern(ValuePattern.Pattern)).Current.Value == "preserved-photo",
                "Transient search state was not restored across the real restart.");
            Require(!HasText(newWindow!, "other-photo.png"), "Restored search did not filter the successor's view.");
            var currentCatalog = new CatalogLocation().Discover().Selection?.DirectoryPath;
            Require(currentCatalog == catalog, "An application update switched away from the current catalog.");
            LocalCatalogState state;
            try { state = await new SqliteDesktopCatalogStore(catalog).LoadAsync(includeItems: false); }
            finally { SqliteConnection.ClearAllPools(); } // Do not retain observer handles across another application start.
            Require(state.BackgroundProcessingPaused && state.ActiveFolder == media && state.ViewMode == "Folder" &&
                !state.IncludeSubfolders && !state.ShowVideos && !state.SortNewestFirst && state.DateGroupingMode == "FileDate",
                "Persistent library view settings changed across update.");
            Assertions["viewRestored"] = true;
            Require(await RowsSnapshotAsync(catalog) == rowsBefore, "Media rows changed across the program update.");
            Assertions["syntheticCatalogPreserved"] = true;
            VerifyOriginals(); Assertions["syntheticOriginalsPreserved"] = true;
            Require(JournalSnapshot(catalog).SequenceEqual(journalsBefore), "Completed file-operation journal bytes changed.");
            Assertions["journalsPreserved"] = true;
            InvokeButton(newWindow!, "Настройки");
            AutomationElement? newSettings = null;
            await UntilAsync(() => (newSettings = FindWindow(successor!.Id, "Настройки PhotoShelf")) is not null &&
                HasText(newSettings, "Текущая версия: PhotoShelf Ultra 1.11.2"), "Running successor does not report its own technical version in the real update panel.");
            CloseWindow(newSettings!);
            CloseWindow(newWindow!);
            await UntilAsync(() => successor!.HasExited && helper!.HasExited, "Successor or update helper did not exit gracefully.");
            Require(successor!.ExitCode == 0 && helper!.ExitCode == 0, "Successor/helper process failed after reporting ready.");
            _phase = "old-shortcut-normal-entrypoint";
            var shortcut = StartOwned(oldExe);
            await UntilAsync(() => shortcut.HasExited, "Old shortcut did not redirect without opening an old catalog writer.");
            Require(shortcut.ExitCode == 0, "Old shortcut redirect failed.");
            Process? redirected = null;
            AutomationElement? redirectedWindow = null;
            await UntilAsync(() =>
            {
                redirected ??= FindOwnedProcess(active.ExecutablePath);
                return redirected is not null && (redirectedWindow = MainWindow(redirected.Id)) is not null && HasText(redirectedWindow, "preserved-photo.png");
            }, "Old shortcut did not open the activated new WPF executable.");
            Require(redirected!.Id != successor.Id, "Shortcut evidence reused the previous process.");
            Assertions["oldShortcutRedirects"] = true;
            CloseWindow(redirectedWindow!);
            await UntilAsync(() => redirected.HasExited, "Redirected application did not close gracefully.");
            Require(redirected.ExitCode == 0, "Redirected successor failed.");
            VerifyOriginals();
            Require(await RowsSnapshotAsync(catalog) == rowsBefore && JournalSnapshot(catalog).SequenceEqual(journalsBefore),
                "A later old-shortcut launch changed preserved rows or journals.");
            Require(ErrorSnapshot(Path.Combine(_profile, "diagnostics", "errors")).Length == 0 &&
                ErrorSnapshot(fallbackErrors).SequenceEqual(fallbackBefore),
                "The real application recorded a normal or fallback error during the lifecycle.");
            Assertions["noApplicationErrors"] = true;
            _phase = "complete";
            status = "passed";
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            Console.Error.WriteLine($"WPF updater lifecycle failed in {_phase}: {exception.Message}");
            PreserveOwnedErrorLogs(Path.GetDirectoryName(reportPath)!);
            try { failureDiagnostics = await CaptureFailureDiagnosticsAsync(); }
            catch (Exception diagnosticError) { failureDiagnostics = new { error = diagnosticError.GetType().Name }; }
        }
        finally
        {
            // Only verified fixture processes can be terminated on failure. The app's retained
            // profile, originals and journals are never deleted or reverted, even in cleanup.
            foreach (var process in OwnedProcesses.DistinctBy(process => process.Id))
            {
                try { if (!process.HasExited && IsOwnedExecutable(process.MainModule?.FileName)) { process.Kill(); process.WaitForExit(10000); } }
                catch (Exception cleanup) { Console.Error.WriteLine("Owned process cleanup: " + cleanup.GetType().Name); }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(output, new
            {
                schema = 1, status, commit = args[2], version = args[3], platform = "windows",
                scope = "production-desktop-update-lifecycle-with-test-trust", sourcePublicKeySubstituted = true,
                sourceFaultCheckpointsInserted = true, raceChecks = RaceEvidence,
                testVersions = new[] { "1.11.1", "1.11.2" }, assertions = Assertions, phase = _phase,
                elapsedSeconds = elapsed.Elapsed.TotalSeconds, processes = ProcessEvidence,
                ownedProfile = _profile, ownedFixtures = _runRoot, error, failureDiagnostics,
                limitations = new[]
                {
                    "Real Desktop and Updater source builds use an ephemeral compile-time test public key and two technical versions; these are not release artifacts.",
                    "Normal entrypoints, production WPF controls and real child processes are exercised on an initially absent disposable CI profile.",
                    "Only owned source copies contain three scheduling checkpoints and a catalog-entry observer; production artifacts have no runtime fault-control switch.",
                    "This scenario verifies the successful WPF transition and old shortcut. Deterministic interruption cases are reported separately by updater-e2e.json.",
                    "Original byte/hash/mtime preservation and media-row/journal preservation are checked; physical power loss, real user libraries and video playback are not simulated."
                }
            }, Json);
            output.Flush(true);
        }
        return status == "passed" ? 0 : 1;
    }

    private static void ClaimEmptyProfile()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _profile = Path.Combine(local, "PhotoShelf");
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoShelf");
        Require(!Directory.Exists(_profile) && !File.Exists(_profile) && !Directory.Exists(roaming) && !File.Exists(roaming),
            "Existing PhotoShelf data must never be used by this test.");
        Require(Process.GetProcessesByName("PhotoShelf").Length == 0 && Process.GetProcessesByName("PhotoShelf.Updater").Length == 0,
            "Another PhotoShelf process is already running.");
        UpdatePackageVerifier.RejectReparseAncestors(local);
        _ownerId = Guid.NewGuid().ToString("N");
        var candidate = Path.Combine(local, "PhotoShelf-WpfUpdateOwner-" + _ownerId);
        Directory.CreateDirectory(Path.Combine(candidate, "updates"));
        File.WriteAllText(Path.Combine(candidate, "updates", "wpf-lifecycle-owner.txt"), _ownerId);
        Directory.Move(candidate, _profile); // Atomic claim: fails if a profile appeared since the check.
    }

    private static async Task<string> SeedAsync(string newPackage, string key)
    {
        _phase = "seed-synthetic-only-profile";
        var media = Directory.CreateDirectory(Path.Combine(_runRoot, "synthetic-media")).FullName;
        // Valid tiny PNG; the separate decoder harness covers format fidelity and video playback.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAL0lEQVR4nO3NQQkAAAgEMJMYx/5RLoIpfAiD/VeZPlUCgUAgeBH05JRAIBAIXgQL70pQTFa2HxYAAAAASUVORK5CYII=");
        var first = Path.Combine(media, "preserved-photo.png");
        var other = Path.Combine(media, "other-photo.png");
        var video = Path.Combine(media, "preserved-video.mp4");
        await File.WriteAllBytesAsync(first, png); await File.WriteAllBytesAsync(other, png);
        await File.WriteAllBytesAsync(video, [0, 0, 0, 16, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0]);
        var store = new SqliteDesktopCatalogStore(_profile);
        await store.InitializeAsync();
        await store.SaveAsync(new LocalCatalogState
        {
            ViewMode = "Folder", ActiveFolder = media, IncludeSubfolders = false, ShowVideos = false,
            IncludeSystemFolders = true, DateGroupingMode = "FileDate", SortNewestFirst = false,
            BackgroundProcessingPaused = true, WatchedFolders = [media], IncludedFolders = [media],
            ExcludedFolders = [Path.GetPathRoot(media)!], TileWidth = 196
        }, saveItems: false);
        await store.UpsertItemsAsync(new[] { first, other, video }.Select(path => new SavedMediaItem
        {
            Path = path, AssetId = Guid.NewGuid().ToString("N"), SizeBytes = new FileInfo(path).Length,
            FileModifiedAt = File.GetLastWriteTime(path), IsVideo = path == video, IsFavorite = path == first,
            MetadataIndexed = true, MetadataStatus = MetadataReadStatus.Absent,
            Availability = FileAvailability.Available, ObservationVersion = 7
        }));
        var journalSource = Path.Combine(media, "journal-witness.bin");
        await File.WriteAllTextAsync(journalSource, "Synthetic completed file-operation evidence; never a user file.");
        var service = new FileMoveService();
        var plan = service.Plan([new MoveRequest(journalSource, null)], Path.Combine(media, "moved"), CollisionPolicy.Skip);
        var moves = await service.ExecuteAsync(plan, Path.Combine(_profile, "operations", "synthetic-completed.jsonl"),
            _ => Task.CompletedTask, null, CancellationToken.None);
        Require(moves.Count == 1 && moves[0].Moved, "Cannot seed a genuine completed synthetic move journal.");
        foreach (var file in Directory.EnumerateFiles(media, "*", SearchOption.AllDirectories))
            Originals.Add(file, (Hash(file), new FileInfo(file).Length, File.GetLastWriteTimeUtc(file).Ticks));
        var paths = new UpdateInstallationPaths();
        var stageId = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(paths.StagingRoot, stageId);
        Directory.CreateDirectory(stage);
        File.Copy(Path.Combine(_runRoot, "PhotoShelf-v1.11.2-ultra-win-x64.zip"), Path.Combine(stage, "package.zip"));
        ZipFile.ExtractToDirectory(Path.Combine(stage, "package.zip"), Path.Combine(stage, "package"));
        File.Copy(Path.Combine(_runRoot, "photoshelf-update.json"), Path.Combine(stage, "photoshelf-update.json"));
        File.Copy(Path.Combine(_runRoot, "photoshelf-update.sig"), Path.Combine(stage, "photoshelf-update.sig"));
        _ = await UpdatePackageVerifier.VerifyStagedAsync(stage, key);
        await new UpdatePreferencesStore(Path.Combine(paths.AppRoot, "updates", "preferences-v1.json"))
            .SaveAsync(new UpdatePreferences { AutoCheck = false, PreparedStageId = stageId });
        return media;
    }

    private static async Task<string> RowsSnapshotAsync(string catalog)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(catalog, "catalog-v2.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using (var integrity = connection.CreateCommand())
        { integrity.CommandText = "PRAGMA quick_check;"; Require((string?)await integrity.ExecuteScalarAsync() == "ok", "Synthetic catalog integrity failed."); }
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM desktop_media_items ORDER BY path_key;";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(values);
        }
        Require(rows.Count == 3, "The synthetic catalog lost or added media rows.");
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rows)));
    }

    private static KeyValuePair<string, string>[] JournalSnapshot(string catalog) => Directory.GetFiles(Path.Combine(catalog, "operations"), "*.jsonl")
        .Order(StringComparer.Ordinal).Select(path => new KeyValuePair<string, string>(Path.GetFileName(path), Hash(path))).ToArray();
    private static void VerifyOriginals()
    {
        foreach (var (path, before) in Originals)
            Require(File.Exists(path) && Hash(path) == before.Hash && new FileInfo(path).Length == before.Length &&
                File.GetLastWriteTimeUtc(path).Ticks == before.Modified, "A synthetic original was lost or changed: " + Path.GetFileName(path));
    }

    private static bool GateReached(string checkpoint) => File.Exists(Path.Combine(_controlRoot, checkpoint + ".reached.json"));
    private static bool HasReadyReceipt(UpdateInstallationPaths paths) => Directory.Exists(paths.OperationsRoot) &&
        Directory.EnumerateFiles(paths.OperationsRoot, "startup-ready.json", SearchOption.AllDirectories).Any();
    private static void RequireGateRecord(string file, string checkpoint, int pid, string version)
    {
        var path = Path.Combine(_controlRoot, file);
        UpdatePackageVerifier.RejectReparseAncestors(path);
        Require(new FileInfo(path).Length <= 2048, "Oversized owned checkpoint marker.");
        var record = JsonSerializer.Deserialize<GateRecord>(File.ReadAllText(path), Json);
        Require(record is not null && record.Checkpoint == checkpoint && record.ProcessId == pid && record.Version == version,
            "Checkpoint does not belong to the expected real application/helper: " + checkpoint);
    }
    private static void ReleaseGate(string checkpoint)
    {
        UpdatePackageVerifier.RejectReparseAncestors(_controlRoot);
        Require(File.ReadAllText(Path.Combine(_controlRoot, "owned-test-control.txt")) == _controlOwner,
            "Refusing to release an unowned scheduling checkpoint.");
        using var release = new FileStream(Path.Combine(_controlRoot, checkpoint + ".release"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        release.WriteByte(1);
        release.Flush(true);
    }
    private static async Task AssertRepeatedLaunchBlockedAsync(string oldExe, string checkpoint, string catalog,
        string rowsBefore, KeyValuePair<string, string>[] journalsBefore)
    {
        const string expectedText = "PhotoShelf запускается или обновляется. Дождитесь завершения и повторите запуск.";
        var repeated = StartOwned(oldExe);
        var catalogMarker = Path.Combine(_controlRoot, "catalog-entry-" + repeated.Id + ".json");
        AutomationElement? busy = null;
        await UntilAsync(() =>
        {
            // Fail fast on the unfixed baseline, instead of accepting an eventual file-lock failure.
            // InvalidDataException is intentionally not one of UntilAsync's transient UI exceptions.
            var enteredCatalog = File.Exists(catalogMarker);
            var openedMainWindow = MainWindow(repeated.Id) is not null;
            if (enteredCatalog || openedMainWindow)
                throw new InvalidDataException($"Repeated old launch entered catalog startup during {checkpoint}. " +
                    $"Process {repeated.Id}: catalogEntryMarker={enteredCatalog}, mainWindow={openedMainWindow}.");
            if (repeated.HasExited)
                throw new InvalidDataException("Repeated launch exited without the required busy prompt during " + checkpoint + ".");
            busy = FindWindow(repeated.Id, "PhotoShelf");
            return busy is not null && HasText(busy, expectedText);
        }, "Repeated old launch did not show the exact update-in-progress message at " + checkpoint + ".", 15);
        var okay = busy!.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1"))) ?? Button(busy, "OK");
        Require(okay is not null && okay.Current.IsEnabled, "Busy prompt has no enabled native OK button.");
        ((InvokePattern)okay!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        await UntilAsync(() => repeated.HasExited, "Dismissed busy launch did not exit at " + checkpoint + ".", 5);
        Require(repeated.ExitCode == 0 && !File.Exists(catalogMarker) && MainWindow(repeated.Id) is null,
            "Busy launch must exit successfully before catalog startup: " + checkpoint);
        VerifyOriginals();
        Require(await RowsSnapshotAsync(catalog) == rowsBefore && JournalSnapshot(catalog).SequenceEqual(journalsBefore),
            "Repeated launch changed synthetic media rows or journals at " + checkpoint + ".");
        RaceEvidence.Add(new(checkpoint, repeated.Id, BusyPromptShown: true, repeated.ExitCode, CatalogStartup: false));
    }

    private static void PreserveOwnedErrorLogs(string reports)
    {
        try
        {
            var marker = Path.Combine(_profile, "updates", "wpf-lifecycle-owner.txt");
            if (_profile.Length == 0 || !File.Exists(marker) || File.ReadAllText(marker) != _ownerId) return;
            var source = Path.Combine(_profile, "diagnostics", "errors");
            if (!Directory.Exists(source)) return;
            var destination = Path.Combine(reports, "wpf-update-errors");
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source).Take(16))
            {
                if (new FileInfo(file).Length > 256 * 1024) continue;
                UpdatePackageVerifier.RejectReparseAncestors(file);
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
            }
        }
        catch (Exception) { /* Failure diagnostics must not replace the original error or modify a profile. */ }
    }

    private static async Task<object> CaptureFailureDiagnosticsAsync()
    {
        // Collect only the fixture processes already established by the ownership checks. Never
        // enumerate unrelated window text or read a catalog while diagnosing a failed startup.
        var processes = new List<object>();
        var liveOwnedIds = new List<int>();
        foreach (var record in ProcessEvidence.Take(16))
        {
            try
            {
                var process = OwnedProcesses.First(candidate => candidate.Id == record.Id);
                var exited = process.HasExited;
                var actual = exited ? null : process.MainModule?.FileName;
                if (!exited && IsOwnedExecutable(actual) && process.StartTime.ToUniversalTime() == record.StartedUtc)
                    liveOwnedIds.Add(record.Id);
                processes.Add(new { record.Id, expectedExecutable = record.Executable, observedModulePath = actual,
                    exited, exitCode = exited ? (int?)process.ExitCode : null });
            }
            catch (Exception exception) { processes.Add(new { record.Id, expectedExecutable = record.Executable, error = exception.GetType().Name }); }
        }
        object windows;
        var uiNodes = new ConcurrentQueue<object>();
        try
        {
            // UI Automation makes synchronous cross-process calls: a hung window must not delay
            // the failure report. The worker reads only and is abandoned after this deadline.
            windows = await Task.Run(() => SnapshotOwnedWindows(liveOwnedIds.ToArray(), uiNodes)).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception exception) { windows = new { error = exception.GetType().Name, nodes = uiNodes.ToArray(), truncated = true }; }
        var marker = Path.Combine(_profile, "updates", "wpf-lifecycle-owner.txt");
        var profileOwned = _profile.Length > 0 && File.Exists(marker) && File.ReadAllText(marker) == _ownerId;
        return new { processes, windows, profileOwned,
            seedDatabaseExists = profileOwned && File.Exists(Path.Combine(_profile, "catalog-v2.sqlite")),
            storageSelectorExists = profileOwned && File.Exists(Path.Combine(_profile, "storage-selection-v1.json")) };
    }

    private static object SnapshotOwnedWindows(int[] processIds, ConcurrentQueue<object> nodes)
    {
        var pending = new Queue<(AutomationElement Element, int Depth, int? Parent)>();
        foreach (var pid in processIds)
            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, pid)).Cast<AutomationElement>().Take(6))
                pending.Enqueue((window, 0, null));
        var walker = TreeWalker.ControlViewWalker;
        var timer = Stopwatch.StartNew();
        while (pending.Count > 0 && nodes.Count < 384 && timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            var (element, depth, parent) = pending.Dequeue();
            try
            {
                var current = element.Current;
                if (!processIds.Contains(current.ProcessId)) continue;
                var index = nodes.Count;
                nodes.Enqueue(new { index, parent, depth, pid = current.ProcessId, name = Clip(current.Name, 512),
                    automationId = Clip(current.AutomationId, 128), controlType = current.ControlType.ProgrammaticName,
                    current.IsEnabled, current.IsOffscreen });
                if (depth >= 16) continue;
                for (var child = walker.GetFirstChild(element); child is not null && nodes.Count + pending.Count < 384;
                    child = walker.GetNextSibling(child))
                    pending.Enqueue((child, depth + 1, index));
            }
            catch (Exception exception) { nodes.Enqueue(new { parent, depth, error = exception.GetType().Name }); }
        }
        return new { nodes = nodes.ToArray(), truncated = pending.Count > 0 };
    }

    private static string Clip(string value, int limit) => value.Length <= limit ? value : value[..limit];

    private static (string Path, long Length, long Modified)[] ErrorSnapshot(string path) => !Directory.Exists(path) ? [] :
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => (file, new FileInfo(file).Length, File.GetLastWriteTimeUtc(file).Ticks)).ToArray();
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void RequireVersion(string path, int patch)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        Require(version.FileMajorPart == 1 && version.FileMinorPart == 11 && version.FileBuildPart == patch,
            "Test executable does not have its expected distinct technical version: " + path);
    }
    private static Process StartOwned(string path)
    {
        Require(IsOwnedExecutable(path), "Refusing to start an unowned executable.");
        var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! })
            ?? throw new IOException("Process did not start.");
        Remember(process, path); return process;
    }
    private static Process? FindOwnedProcess(string path)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            try
            {
                if (!process.HasExited && string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                { Remember(process, path); return process; }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
        return null;
    }
    private static void Remember(Process process, string path)
    {
        if (OwnedProcesses.Any(owned => owned.Id == process.Id)) return;
        Require(IsOwnedExecutable(path), "Discovered process is not owned by this fixture.");
        // Enumerated Process objects have no retained native handle. Keep it while the process
        // is alive so Windows can provide its exit code after it closes; HasExited alone cannot.
        Require(!process.SafeHandle.IsInvalid, "Cannot retain the owned process handle for exit verification.");
        OwnedProcesses.Add(process);
        ProcessEvidence.Add(new(process.Id, process.StartTime.ToUniversalTime(), path));
    }
    private static bool IsOwnedExecutable(string? path) => path is not null &&
        (path.StartsWith(_runRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
         _profile.Length > 0 && File.Exists(Path.Combine(_profile, "updates", "wpf-lifecycle-owner.txt")) &&
         File.ReadAllText(Path.Combine(_profile, "updates", "wpf-lifecycle-owner.txt")) == _ownerId &&
         path.StartsWith(Path.Combine(_profile, "program", "versions") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    private static AutomationElement? MainWindow(int pid) => AutomationElement.RootElement.FindAll(TreeScope.Children,
        new PropertyCondition(AutomationElement.ProcessIdProperty, pid)).Cast<AutomationElement>()
        .FirstOrDefault(window => window.Current.Name.StartsWith("PhotoShelf Ultra", StringComparison.Ordinal));
    private static AutomationElement? FindWindow(int pid, string name)
    {
        var condition = new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, pid),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
            new PropertyCondition(AutomationElement.NameProperty, name));
        // WPF owned dialogs are children of their owner in UI Automation, not desktop siblings.
        // Search only this process's application subtrees; never walk the full desktop subtree.
        foreach (AutomationElement root in AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
            if (root.FindFirst(TreeScope.Subtree, condition) is { } window) return window;
        return null;
    }
    private static AutomationElement FindById(AutomationElement parent, string id) => parent.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, id)) ?? throw new IOException("Missing automation target: " + id);
    private static AutomationElement? Button(AutomationElement parent, string name) => parent.FindFirst(TreeScope.Descendants,
        new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, name)));
    private static bool HasText(AutomationElement parent, string text) => parent.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.NameProperty, text)) is not null;
    private static void InvokeButton(AutomationElement parent, string name)
    {
        var button = Button(parent, name) ?? throw new IOException("Missing application button: " + name);
        Require(button.Current.IsEnabled, "Application button is disabled: " + name);
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }
    private static void CloseWindow(AutomationElement window) => ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
    private static async Task UntilAsync(Func<bool> condition, string message, int seconds = 90)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            try { if (condition()) return; }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException(message);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
