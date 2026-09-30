using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Updates;
using PhotoShelf.Application.Updates.Installation;

namespace PhotoShelf.UpdateLifecycleHarness;

// This executable is an isolated test fixture, never a shipping PhotoShelf component. All controls,
// roots and trust configuration live here; the production updater has no such command-line switches.
internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    internal static string Version => typeof(Program).Assembly.GetName().Version!.ToString(3);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The lifecycle harness requires real Windows processes and file sharing.");
            if (args is ["--run", var oldExe, var newExe, var report, var commit, var releaseVersion])
                return await LifecycleRunner.RunAsync(oldExe, newExe, report, commit, releaseVersion);
            if (args is ["--fixture-version", var versionReport])
            { WriteNewJson(versionReport, new { version = Version, assembly = typeof(Program).Assembly.GetName().Name }); return 0; }
            var config = LoadConfig();
            if (args is ["--verify-startup-resources", "--startup-report", var startupReport])
            {
                // Real isolated child process and report protocol, but no WPF/resource claim: the report
                // identifies this fixture explicitly. Production WPF smoke is a separate CI requirement.
                WriteNewJson(startupReport, new { status = "passed", check = "startup-resources", version = Version,
                    fixture = "console-application-process", syntheticRoot = config.Root });
                return 0;
            }
            if (args is ["--fixture-parent", var stageDirectory]) return await RunParentAsync(config, stageDirectory);
            if (args is ["--install", var requestPath]) return await RunHelperAsync(config, requestPath);
            if (args.Length != 0) throw new ArgumentException("Invalid fixture arguments.");
            if (UpdateLaunchRedirector.TryLaunchNewer(new(config.Root), config.PublicKey, Version)) return 0;
            return RunApplication(config);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static FixtureConfig LoadConfig()
    {
        var config = JsonSerializer.Deserialize<FixtureConfig>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixture-config.json")), Json)
            ?? throw new InvalidDataException("Missing fixture configuration.");
        if (!Path.IsPathFullyQualified(config.Root) || !File.Exists(Path.Combine(config.Root, "fixture-owned-root.txt")))
            throw new InvalidDataException("The fixture can access only its explicitly marked synthetic root.");
        return config;
    }

    private static async Task<int> RunParentAsync(FixtureConfig config, string stageDirectory)
    {
        var control = Path.Combine(config.Root, "control");
        var paths = new UpdateInstallationPaths(config.Root);
        // The helper must wait for actual process exit, even after these handles are drained.
        var catalog = new FileStream(CatalogPath(config.Root), FileMode.Open, FileAccess.Read, FileShare.None);
        var original = new FileStream(MediaPath(config.Root), FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            WriteNewJson(Path.Combine(control, "parent-ready.json"), new { processId = Environment.ProcessId, version = Version });
            await WaitForFileAsync(Path.Combine(control, "consent.signal"), TimeSpan.FromSeconds(45));
            var stage = await UpdatePackageVerifier.VerifyStagedAsync(stageDirectory, config.PublicKey);
            var request = await UpdateInstallRequest.CreateFileAsync(stage, paths, Version);
            WriteNewJson(Path.Combine(control, "request-ready.json"), new { path = request, processId = Environment.ProcessId });
            await WaitForFileAsync(Path.Combine(control, "drain.signal"), TimeSpan.FromSeconds(45));
        }
        finally { catalog.Dispose(); original.Dispose(); }
        WriteNewJson(Path.Combine(control, "parent-drained.json"), new { processId = Environment.ProcessId });
        // Keep the actual parent alive after handle release to exercise WaitForExit rather than a cancellation token.
        await WaitForFileAsync(Path.Combine(control, "exit.signal"), TimeSpan.FromSeconds(45));
        return 0;
    }

    private static async Task<int> RunHelperAsync(FixtureConfig config, string requestPath)
    {
        _ = UpdateHelperArguments.Parse(["--install", requestPath]);
        var control = Path.Combine(config.Root, "control");
        var id = Path.GetFileNameWithoutExtension(requestPath);
        var resultPath = Path.Combine(control, "helper-result-" + id + "-" + Environment.ProcessId + ".json");
        var pausePath = Path.Combine(control, "helper-pause.json");
        var pause = File.Exists(pausePath) ? JsonSerializer.Deserialize<PauseControl>(File.ReadAllText(pausePath), Json) : null;
        try
        {
            void Checkpoint(string step)
            {
                WriteNewJson(Path.Combine(control, "checkpoint-" + id + "-" + step + ".json"), new
                { step, processId = Environment.ProcessId, atUtc = DateTimeOffset.UtcNow });
                if (pause?.Checkpoint != step) return;
                // Only this fixture can pause at a fault boundary. The production helper supplies no callback.
                var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
                while (!File.Exists(Path.Combine(control, "release-helper.signal")))
                {
                    if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The fixture fault gate was not released.");
                    Thread.Sleep(25);
                }
            }
            var options = new UpdateInstallationOptions
            {
                ParentExitTimeout = TimeSpan.FromSeconds(45), StartupVerificationTimeout = TimeSpan.FromSeconds(30),
                StartupHealthTimeout = TimeSpan.FromSeconds(10), Checkpoint = Checkpoint,
                // Synchronous fixture progress pauses after a real candidate process was started. Production
                // uses normal UI progress; there is no fault environment variable or command in the helper.
                Progress = new SynchronousFixtureProgress(step =>
                { if (step == "application-started" && pause?.Checkpoint == step) Checkpoint(step); })
            };
            // These are the real production installer and OS process implementation, not fakes.
            var result = await new UpdateInstaller(new(config.Root), config.PublicKey, Version,
                new UpdateProcessHost(), options).InstallAsync(requestPath);
            WriteNewJson(resultPath, new { status = "passed", result.StartupReady, result.Target.Version, result.Target.ExecutablePath });
            return 0;
        }
        catch (Exception exception)
        {
            WriteNewJson(resultPath, new { status = "rejected", errorType = exception.GetType().Name, error = exception.Message });
            return 3;
        }
    }

    private static int RunApplication(FixtureConfig config)
    {
        var integrity = ReadCatalogIntegrity(config.Root);
        if (integrity != "ok") throw new InvalidDataException("The synthetic SQLite catalog is not readable.");
        if (ReadCatalogMarker(config.Root) != "preserve-library") throw new InvalidDataException("The synthetic catalog content changed.");
        using (var input = new FileStream(MediaPath(config.Root), FileMode.Open, FileAccess.Read, FileShare.Read))
            if (input.Length == 0) throw new InvalidDataException("The synthetic original is empty.");
        UpdateStartupHealth.ReportReady(Environment.ProcessPath!, new(config.Root));
        WriteNewJson(Path.Combine(config.Root, "control", "launch-" + Guid.NewGuid().ToString("N") + ".json"),
            new { version = Version, processId = Environment.ProcessId, sqliteIntegrity = integrity, atUtc = DateTimeOffset.UtcNow,
                executable = Environment.ProcessPath });
        return 0;
    }

    internal static string CatalogPath(string root) => Path.Combine(root, "storage-generations", "synthetic", "catalog-v2.sqlite");
    internal static string MediaPath(string root) => Path.Combine(root, "synthetic-media", "original.mov");
    internal static string ReadCatalogIntegrity(string root)
    {
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = CatalogPath(root), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        database.Open(); using var command = database.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
        return Convert.ToString(command.ExecuteScalar()) ?? "missing";
    }
    internal static string ReadCatalogMarker(string root)
    {
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = CatalogPath(root), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        database.Open(); using var command = database.CreateCommand(); command.CommandText = "SELECT value FROM fixture_metadata WHERE name='marker'";
        return Convert.ToString(command.ExecuteScalar()) ?? "missing";
    }
    internal static void WriteNewJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = path + ".pending-" + Guid.NewGuid().ToString("N");
        using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(output, value, Json); output.Flush(true); }
        File.Move(pending, path, overwrite: false);
    }
    internal static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (!File.Exists(path))
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("Fixture control file was not produced: " + Path.GetFileName(path));
            await Task.Delay(25);
        }
    }
}

internal sealed record FixtureConfig(string Root, string PublicKey);
internal sealed record PauseControl(string Checkpoint);

internal sealed class SynchronousFixtureProgress(Action<string> report) : IProgress<string>
{ public void Report(string value) => report(value); }
