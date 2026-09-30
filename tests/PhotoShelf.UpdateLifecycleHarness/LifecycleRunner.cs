using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Updates;
using PhotoShelf.Application.Updates.Installation;

namespace PhotoShelf.UpdateLifecycleHarness;

internal sealed class LifecycleRunner : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-updater-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _key = RSA.Create(2048); // Ephemeral test key remains in memory and is never exported or uploaded.
    private readonly List<Process> _ownedProcesses = [];
    private readonly List<ScenarioResult> _scenarios = [];
    private readonly List<object> _processEvidence = [];
    private string _oldVersion = "", _newVersion = "";
    private bool _hashesPreserved, _sqliteIntact;
    private readonly string _oldExe, _newExe;
    private LifecycleRunner(string oldExe, string newExe) { _oldExe = Path.GetFullPath(oldExe); _newExe = Path.GetFullPath(newExe); Directory.CreateDirectory(_root); }
    private string PublicKey => _key.ExportSubjectPublicKeyInfoPem();

    public static async Task<int> RunAsync(string oldExe, string newExe, string report, string commit, string releaseVersion)
    {
        using var runner = new LifecycleRunner(oldExe, newExe);
        string status = "failed";
        string? error = null;
        try
        {
            runner._oldVersion = await runner.ReadExecutableVersionAsync(oldExe, "old");
            runner._newVersion = await runner.ReadExecutableVersionAsync(newExe, "new");
            Require(UpdateVersion.IsNewer(runner._newVersion, runner._oldVersion), "The actual compiled candidate must be newer than the actual compiled parent.");
            await runner.RunSuccessfulUpgradeAsync();
            await runner.RunInterruptedUpgradeAsync("before-pointer-publish", "helper-kill-before-activation", false);
            await runner.RunInterruptedUpgradeAsync("application-started", "helper-kill-after-activation", true);
            runner._hashesPreserved = runner._sqliteIntact = true;
            status = "passed";
        }
        catch (Exception exception) { error = exception.ToString(); Console.Error.WriteLine(error); }
        Program.WriteNewJson(Path.GetFullPath(report), new
        {
            schema = 1, scope = "production-updater-process-lifecycle-with-fixture-applications", status, commit,
            version = releaseVersion, platform = "windows", oldAssemblyVersion = runner._oldVersion, newAssemblyVersion = runner._newVersion,
            originalHashesPreserved = runner._hashesPreserved, sqliteIntegrityPassed = runner._sqliteIntact,
            productionComponents = new[] { "UpdateInstaller", "UpdateProcessHost", "UpdateInstallRequest", "UpdatePackageVerifier",
                "UpdateManifestVerifier", "ActiveInstallationResolver", "UpdateLaunchRedirector", "UpdateStartupHealth" },
            fixtureComponents = new[] { "console parent/application/helper entry points", "resource-check child report", "synthetic SQLite schema",
                "ephemeral signing key", "fixture-only root and fault checkpoints" },
            scenarios = runner._scenarios, processes = runner._processEvidence, syntheticRoot = runner._root, error,
            limitations = new[] { "The console fixture does not prove the shipped WPF consent, shutdown or view restoration UI.",
                "Both versioned fixture binaries use the current production installer source; this is not a test of a historical release binary.",
                "Process termination tests do not simulate power loss, storage controller failure or physical disk corruption." }
        });
        return status == "passed" ? 0 : 1;
    }

    private async Task RunSuccessfulUpgradeAsync()
    {
        var scenario = await CreateScenarioAsync("success");
        var (parent, requestPath) = await StartConsentedParentAsync(scenario);
        using var helper = Start(scenario.ShortcutHelper, "--install", requestPath);
        await AssertParentDrainAsync(scenario, parent, requestPath);
        await WaitExitAsync(helper, 0, TimeSpan.FromSeconds(90));
        var helperReport = ReadJson(HelperResultPath(scenario, requestPath, helper.Id));
        Require(helperReport.GetProperty("status").GetString() == "passed" && helperReport.GetProperty("startupReady").GetBoolean(),
            "The production helper must observe the actual candidate's health receipt.");
        var active = Resolve(scenario);
        Require(active.Version == _newVersion && active.ExecutablePath != scenario.Previous.ExecutablePath, "A separately installed newer program must become active.");
        Require(File.Exists(scenario.Previous.ExecutablePath), "The previous program must remain available.");
        var launches = await WaitForLaunchesAsync(scenario, 1);
        Require(launches.All(item => item.GetProperty("version").GetString() == _newVersion), "The installer must launch the actual newer binary.");
        Add("verified-two-version-install-and-health", new { helperId = helper.Id, parentId = parent.Id, active.Version, active.InstallationId,
            candidatePid = launches[0].GetProperty("processId").GetInt32(), stageRetained = File.Exists(scenario.NewStage.PackagePath) });

        var count = launches.Count;
        using (var shortcut = Start(scenario.ShortcutExe)) await WaitExitAsync(shortcut, 0, TimeSpan.FromSeconds(20));
        launches = await WaitForLaunchesAsync(scenario, count + 1);
        Require(launches.All(item => item.GetProperty("version").GetString() == _newVersion), "An old shortcut must run the active new version.");
        Add("old-shortcut-launches-active-version", new { shortcutVersion = _oldVersion, activeVersion = _newVersion, launches = launches.Count });

        using (var replay = Start(scenario.ShortcutHelper, "--install", requestPath))
        {
            await WaitExitAsync(replay, 3, TimeSpan.FromSeconds(20));
            var rejected = ReadJson(HelperResultPath(scenario, requestPath, replay.Id));
            Require(rejected.GetProperty("status").GetString() == "rejected", "A used request must not be replayed.");
        }
        Require(Resolve(scenario) == active, "Replay must not change the active version.");
        Add("used-request-replay-rejected", new { active.Version, active.InstallationId });

        // A former version cannot be made into an authorized update even by explicitly invoking the helper.
        var downgrade = new UpdateInstallRequest(1, Guid.NewGuid().ToString("N"), scenario.OldStage.StageDirectory,
            Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, DateTimeOffset.UtcNow,
            _newVersion, _oldVersion, scenario.OldStage.Release.Manifest.PackageSha256);
        var downgradePath = Path.Combine(scenario.Paths.RequestsRoot, downgrade.RequestId + ".json");
        Program.WriteNewJson(downgradePath, downgrade);
        using (var rejectedHelper = Start(active.ExecutablePath, "--install", downgradePath))
        {
            await WaitExitAsync(rejectedHelper, 3, TimeSpan.FromSeconds(20));
            var rejected = ReadJson(HelperResultPath(scenario, downgradePath, rejectedHelper.Id));
            Require(rejected.GetProperty("errorType").GetString() == nameof(InvalidDataException), "The older signed candidate must be rejected by the request contract.");
        }
        Require(Resolve(scenario) == active, "Downgrade must not change the active program.");
        Add("downgrade-rejected", new { offeredVersion = _oldVersion, currentVersion = _newVersion });
        AssertDataUnchanged(scenario);
    }

    private async Task RunInterruptedUpgradeAsync(string checkpoint, string name, bool activated)
    {
        var scenario = await CreateScenarioAsync(name);
        Program.WriteNewJson(Path.Combine(scenario.Control, "helper-pause.json"), new PauseControl(checkpoint));
        var (parent, request) = await StartConsentedParentAsync(scenario);
        using var helper = Start(scenario.ShortcutHelper, "--install", request);
        await AssertParentDrainAsync(scenario, parent, request, recordScenario: false);
        var id = Path.GetFileNameWithoutExtension(request);
        await Program.WaitForFileAsync(Path.Combine(scenario.Control, "checkpoint-" + id + "-" + checkpoint + ".json"), TimeSpan.FromSeconds(90));
        var atBoundary = Resolve(scenario);
        Require(atBoundary.Version == (activated ? _newVersion : _oldVersion), "The fault gate must be reached at the requested activation boundary.");
        if (activated)
        {
            var candidateLaunch = await WaitForLaunchesAsync(scenario, 1);
            Require(candidateLaunch.All(item => item.GetProperty("version").GetString() == _newVersion),
                "After-activation interruption must occur after a real newer application read the SQLite catalog and published health.");
        }
        Require(!helper.HasExited, "The helper must still be a living process when the crash is injected.");
        helper.Kill(); // Only this Process handle, created and tracked by this runner. Never kill by name or PID search.
        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var afterKill = Resolve(scenario);
        Require(afterKill == atBoundary, "Process death must retain the last complete active pointer.");
        Require(File.Exists(scenario.Previous.ExecutablePath), "The old executable must survive interruption.");
        Require(File.Exists(scenario.NewStage.PackagePath), "The signed staging package must survive interruption.");
        AssertDataUnchanged(scenario);

        // Normal old-shortcut startup must obey the committed pointer and must never resume a pending request.
        var launchesBeforeRestart = Directory.GetFiles(scenario.Control, "launch-*.json").Length;
        using (var shortcut = Start(scenario.ShortcutExe)) await WaitExitAsync(shortcut, 0, TimeSpan.FromSeconds(20));
        var launches = await WaitForLaunchesAsync(scenario, launchesBeforeRestart + 1);
        Require(launches.All(item => item.GetProperty("version").GetString() == afterKill.Version), "Recovery startup must use exactly the retained pointer version.");
        using (var replay = Start(scenario.ShortcutHelper, "--install", request)) await WaitExitAsync(replay, 3, TimeSpan.FromSeconds(20));
        Require(Resolve(scenario) == afterKill, "A crashed single-use request must not be resumed automatically or replayed.");
        AssertDataUnchanged(scenario);
        Add(name, new { checkpoint, killedProcessId = helper.Id, activeVersion = afterKill.Version,
            resumedAutomatically = false, candidateStartedBeforeInterruption = activated,
            originalAndCatalogHashesPreserved = true, sqliteIntegrity = "ok" });
    }

    private async Task<(Process Parent, string Request)> StartConsentedParentAsync(Scenario scenario)
    {
        var parent = Start(scenario.ShortcutExe, "--fixture-parent", scenario.NewStage.StageDirectory);
        await Program.WaitForFileAsync(Path.Combine(scenario.Control, "parent-ready.json"), TimeSpan.FromSeconds(20));
        Require(!Directory.Exists(scenario.Paths.RequestsRoot) || !Directory.EnumerateFiles(scenario.Paths.RequestsRoot).Any(),
            "Starting the old application must not create an installation request before consent.");
        RequireExclusiveReadIsBlocked(Program.CatalogPath(scenario.Root));
        RequireExclusiveReadIsBlocked(Program.MediaPath(scenario.Root));
        Signal(scenario, "consent.signal");
        await Program.WaitForFileAsync(Path.Combine(scenario.Control, "request-ready.json"), TimeSpan.FromSeconds(30));
        var path = ReadJson(Path.Combine(scenario.Control, "request-ready.json")).GetProperty("path").GetString()!;
        var request = JsonSerializer.Deserialize<UpdateInstallRequest>(File.ReadAllText(path), Program.Json)!;
        Require(request.ParentProcessId == parent.Id && request.ExpectedVersion == _newVersion, "The explicit request must identify the actual old parent and newer signed version.");
        return (parent, path);
    }

    private async Task AssertParentDrainAsync(Scenario scenario, Process parent, string request, bool recordScenario = true)
    {
        var id = Path.GetFileNameWithoutExtension(request);
        var waiting = Path.Combine(scenario.Paths.OperationsRoot, id, "0001-waiting-for-parent.json");
        await Program.WaitForFileAsync(waiting, TimeSpan.FromSeconds(20));
        await Task.Delay(250);
        Require(!parent.HasExited && Resolve(scenario) == scenario.Previous, "A living parent must prevent activation.");
        Require(Directory.GetDirectories(scenario.Paths.VersionsRoot).Length == 1, "A living parent must prevent program copy.");
        RequireExclusiveReadIsBlocked(Program.CatalogPath(scenario.Root));
        Signal(scenario, "drain.signal");
        await Program.WaitForFileAsync(Path.Combine(scenario.Control, "parent-drained.json"), TimeSpan.FromSeconds(10));
        using (new FileStream(Program.CatalogPath(scenario.Root), FileMode.Open, FileAccess.Read, FileShare.None)) { }
        await Task.Delay(250);
        Require(!parent.HasExited && Resolve(scenario) == scenario.Previous, "Drained handles alone are not actual parent exit.");
        Require(!File.Exists(Path.Combine(scenario.Control, "checkpoint-" + id + "-parent-exited.json")), "The process host must not mistake handle drain for process exit.");
        Signal(scenario, "exit.signal");
        await WaitExitAsync(parent, 0, TimeSpan.FromSeconds(10));
        if (recordScenario) Add("explicit-consent-and-parent-drain", new { parentId = parent.Id, explicitConsent = true,
            heldWindowsFileLocks = true, actualExitRequired = true });
    }

    private async Task<Scenario> CreateScenarioAsync(string name)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "fixture-owned-root.txt"), "Only synthetic updater lifecycle evidence. Never a user library.");
        var control = Path.Combine(root, "control"); Directory.CreateDirectory(control);
        var paths = new UpdateInstallationPaths(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Program.CatalogPath(root))!);
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Program.CatalogPath(root), Pooling = false }.ToString()))
        {
            database.Open(); using var command = database.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; CREATE TABLE fixture_metadata(name TEXT PRIMARY KEY,value TEXT NOT NULL); INSERT INTO fixture_metadata VALUES('marker','preserve-library');";
            command.ExecuteNonQuery();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Program.MediaPath(root))!);
        File.WriteAllBytes(Program.MediaPath(root), RandomNumberGenerator.GetBytes(32 * 1024));
        var photo = Path.Combine(root, "synthetic-media", "original.heic"); File.WriteAllBytes(photo, RandomNumberGenerator.GetBytes(16 * 1024));
        var journal = Path.Combine(root, "storage-generations", "synthetic", "operations", "move.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, "{\"fixture\":true,\"state\":\"preserve-unfinished-operation\"}\n");
        var evidence = new[] { Program.CatalogPath(root), Program.MediaPath(root), photo, journal }.ToDictionary(path => path, HashFile);
        var oldStage = await CreateStageAsync(root, _oldExe, _oldVersion);
        var newStage = await CreateStageAsync(root, _newExe, _newVersion);
        var installationId = Guid.NewGuid().ToString("N");
        var installation = Path.Combine(paths.VersionsRoot, installationId); var app = Path.Combine(installation, "app");
        CopyTree(oldStage.PackageDirectory, app);
        File.Copy(oldStage.ManifestPath, Path.Combine(installation, "photoshelf-update.json"));
        File.Copy(oldStage.SignaturePath, Path.Combine(installation, "photoshelf-update.sig"));
        Program.WriteNewJson(paths.ActivePointerPath, new ActiveInstallationPointer(1, installationId, _oldVersion));
        var shortcut = Path.Combine(root, "original-distribution"); Directory.CreateDirectory(shortcut);
        File.Copy(_oldExe, Path.Combine(shortcut, "PhotoShelf.exe"));
        File.Copy(_oldExe, Path.Combine(shortcut, "PhotoShelf.Updater.exe"));
        Program.WriteNewJson(Path.Combine(shortcut, "fixture-config.json"), new FixtureConfig(root, PublicKey));
        var previous = ActiveInstallationResolver.Resolve(paths, PublicKey)!;
        return new(root, control, paths, oldStage, newStage, previous, Path.Combine(shortcut, "PhotoShelf.exe"),
            Path.Combine(shortcut, "PhotoShelf.Updater.exe"), evidence);
    }

    private async Task<StagedUpdate> CreateStageAsync(string root, string executable, string version)
    {
        var stage = Path.Combine(new UpdateInstallationPaths(root).StagingRoot, Guid.NewGuid().ToString("N"));
        var package = Path.Combine(stage, "package"); Directory.CreateDirectory(package);
        File.Copy(executable, Path.Combine(package, "PhotoShelf.exe"));
        File.Copy(executable, Path.Combine(package, "PhotoShelf.Updater.exe"));
        File.WriteAllText(Path.Combine(package, "RUNNING.txt"), $"PhotoShelf Ultra v{version} — Windows x64\nSynthetic test fixture, not a PhotoShelf distribution.\n");
        Program.WriteNewJson(Path.Combine(package, "fixture-config.json"), new FixtureConfig(root, PublicKey));
        foreach (var path in new[] { "codecs/heif/PhotoShelf.HeifWorker.exe", "codecs/heif/heif.dll", "codecs/heif/libde265.dll",
                     "codecs/heif/VERSION.txt", "codecs/heif/sources/sources.json", "licenses/NOTICE.txt", "codecs/heif/licenses/LICENSE.txt" })
        {
            var full = Path.Combine(package, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, "Synthetic inert fixture; never loaded as a codec.");
        }
        var files = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new { path = Path.GetRelativePath(package, path).Replace(Path.DirectorySeparatorChar, '/'), length = new FileInfo(path).Length, sha256 = HashFile(path) }).ToArray();
        var inventoryPath = Path.Combine(package, "package-manifest.json");
        Program.WriteNewJson(inventoryPath, new { schema = 1, product = "PhotoShelf Ultra", version = version + "-ultra", commit = new string('a', 40), files });
        var zip = Path.Combine(stage, "package.zip");
        ZipFile.CreateFromDirectory(package, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
        var manifest = new UpdateManifest { ProtocolVersion = 1, Version = version, Runtime = "win-x64",
            PackageUrl = $"https://github.com/BigGluk23/PhotoShelf/releases/download/v{version}-ultra/PhotoShelf-v{version}-ultra-win-x64.zip",
            PackageSha256 = HashFile(zip), PackageManifestSha256 = HashFile(inventoryPath), PackageBytes = new FileInfo(zip).Length,
            UnpackedBytes = files.Sum(file => file.length) + new FileInfo(inventoryPath).Length, MinCatalogSchema = 5, MaxCatalogSchema = 5,
            ReleaseNotesUrl = $"https://github.com/BigGluk23/PhotoShelf/releases/tag/v{version}-ultra" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Program.Json);
        File.WriteAllBytes(Path.Combine(stage, "photoshelf-update.json"), bytes);
        File.WriteAllBytes(Path.Combine(stage, "photoshelf-update.sig"), _key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return await UpdatePackageVerifier.VerifyStagedAsync(stage, PublicKey);
    }

    private async Task<string> ReadExecutableVersionAsync(string executable, string kind)
    {
        var path = Path.Combine(_root, kind + "-version.json");
        using var process = Start(executable, "--fixture-version", path);
        await WaitExitAsync(process, 0, TimeSpan.FromSeconds(30));
        return ReadJson(path).GetProperty("version").GetString()!;
    }
    private Process Start(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove(UpdateStartupHealth.RequestVariable); start.Environment.Remove(UpdateStartupHealth.InstallationVariable);
        var process = Process.Start(start) ?? throw new IOException("Fixture process could not start.");
        _ownedProcesses.Add(process);
        _processEvidence.Add(new { processId = process.Id, executable, command = arguments.FirstOrDefault() ?? "normal-start", atUtc = DateTimeOffset.UtcNow });
        return process;
    }
    private static async Task WaitExitAsync(Process process, int expectedCode, TimeSpan timeout)
    {
        await process.WaitForExitAsync().WaitAsync(timeout);
        Require(process.ExitCode == expectedCode, $"Process {process.Id} exited {process.ExitCode}, expected {expectedCode}.");
    }
    private static void RequireExclusiveReadIsBlocked(string path)
    {
        try { using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (IOException) { return; }
        throw new InvalidOperationException("The old process did not retain the expected Windows file lock.");
    }
    private static void Signal(Scenario scenario, string name) => File.WriteAllText(Path.Combine(scenario.Control, name), "explicit fixture action");
    private ActiveInstallationTarget Resolve(Scenario scenario) => ActiveInstallationResolver.Resolve(scenario.Paths, PublicKey)
        ?? throw new InvalidDataException("The complete active pointer disappeared.");
    private static string HelperResultPath(Scenario scenario, string request, int pid) => Path.Combine(scenario.Control,
        "helper-result-" + Path.GetFileNameWithoutExtension(request) + "-" + pid + ".json");
    private static JsonElement ReadJson(string path) { using var doc = JsonDocument.Parse(File.ReadAllText(path)); return doc.RootElement.Clone(); }
    private static async Task<List<JsonElement>> WaitForLaunchesAsync(Scenario scenario, int minimum)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(20);
        while (true)
        {
            var files = Directory.GetFiles(scenario.Control, "launch-*.json");
            if (files.Length >= minimum) return files.Select(ReadJson).ToList();
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("The actual candidate did not write its launch receipt.");
            await Task.Delay(25);
        }
    }
    private static void AssertDataUnchanged(Scenario scenario)
    {
        foreach (var (file, hash) in scenario.Evidence) Require(HashFile(file) == hash, "Synthetic original/catalog/journal bytes changed: " + Path.GetFileName(file));
        Require(Program.ReadCatalogIntegrity(scenario.Root) == "ok", "SQLite integrity check failed.");
        Require(Program.ReadCatalogMarker(scenario.Root) == "preserve-library", "SQLite marker changed.");
    }
    private void Add(string name, object details) { _scenarios.Add(new(name, "passed", details)); Console.WriteLine("PASS " + name); }
    private static string HashFile(string path) { using var input = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(input)); }
    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        { var output = Path.Combine(destination, Path.GetRelativePath(source, path)); Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(path, output); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public void Dispose()
    {
        // A failing test may leave one of its own fixture children at a fault gate. Never scan/kill unrelated processes.
        foreach (var process in _ownedProcesses)
        {
            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } } catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        _key.Dispose();
        // Preserve owned synthetic evidence for diagnosis. CI uploads only the compact report, never this directory.
    }
    private sealed record Scenario(string Root, string Control, UpdateInstallationPaths Paths, StagedUpdate OldStage,
        StagedUpdate NewStage, ActiveInstallationTarget Previous, string ShortcutExe, string ShortcutHelper, Dictionary<string, string> Evidence);
    private sealed record ScenarioResult(string Name, string Status, object Details);
}
