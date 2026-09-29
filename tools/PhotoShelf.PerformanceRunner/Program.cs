using System.ComponentModel;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Desktop;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.PerformanceRunner;

/// <summary>One identical host runs against either source revision. It never selects an existing catalog.</summary>
internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<Sample> Samples = [];
    private static readonly List<MemorySample> Memory = [];
    private static readonly List<object> Supersessions = [];
    private static readonly List<ProjectionTimingSample> ProjectionTimings = [];
    private static readonly List<PhaseTiming> Phases = [];
    private static readonly List<object> FolderDiagnostics = [];
    private const int SeedCacheKiB = 65536;
    private static readonly object SetupProfile = new
    {
        name = "seed-connection-cache-64mib-v1", seedCacheKiB = SeedCacheKiB, seedConnectionPooling = false,
        synchronous = "FULL", journalMode = "WAL", checkpoint = "TRUNCATE", queryCache = "production-default"
    };
    private static string _mediaRoot = "";
    private static int _count;
    private static int _repetitions;
    private static string _reportPath = "";
    private static string _revision = "";
    private static string _label = "";
    private static int _cancellations;

    [STAThread]
    public static int Main(string[] args)
    {
        // Parse/reserve output before initializing any application/catalog/cache.
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Actual WPF timings require Windows.");
        if (args.Length != 5 || !int.TryParse(args[0], out _count) || _count is not (100_000 or 1_000_000) ||
            !int.TryParse(args[1], out _repetitions) || _repetitions is < 5 or > 50)
            throw new ArgumentException("Usage: count(100000|1000000) repetitions(5..50) report.json revision label");
        _reportPath = Path.GetFullPath(args[2]); _revision = args[3]; _label = args[4];
        using var report = new FileStream(_reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var catalog = LocalCatalogStore.CreateIsolatedSmokeCatalog();
        _mediaRoot = Path.Combine(catalog, "synthetic-catalog-only");
        typeof(ErrorReporter).GetProperty("AutomatedCheck", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, true);
        string? failure = null;
        var exitCode = 1;
        CatalogSnapshot? catalogBefore = null, catalogAfter = null;
        try
        {
            var store = new SqliteDesktopCatalogStore();
            SeedAsync(store).GetAwaiter().GetResult();
            MeasureSqlAsync(store).GetAwaiter().GetResult();
            var app = new App(verificationOnly: true) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                MainWindow? window = null;
                try
                {
                    using (var phase = BeginPhase("startup-and-quiesce"))
                    {
                        var state = await MainWindow.LoadInitialCatalogStateAsync();
                        window = new MainWindow(state) { Width = 1280, Height = 800, WindowState = WindowState.Normal };
                        app.MainWindow = window;
                        window.Show();
                        var ready = (Task<Exception?>)typeof(MainWindow).GetProperty("InitialCatalogReady", PrivateInstance)!.GetValue(window)!;
                        if (await ready.WaitAsync(TimeSpan.FromSeconds(120)) is { } error) throw new InvalidOperationException("Startup failed.", error);
                        // Identical controlled profile: wait for real readers to stop, not just request cancellation.
                        await QuiesceAsync(window);
                        await WaitForIdleAsync(window);
                        phase.Complete();
                    }
                    using (var phase = BeginPhase("integrity-before"))
                    { catalogBefore = await SnapshotCatalogAsync(); phase.Complete(); }
                    await MeasureUiAsync(window);
                    using (var phase = BeginPhase("final-quiesce"))
                    { await QuiesceAsync(window); await WaitForIdleAsync(window); phase.Complete(); }
                    using (var phase = BeginPhase("integrity-after"))
                    { catalogAfter = await SnapshotCatalogAsync(); phase.Complete(); }
                    if (catalogBefore != catalogAfter || catalogBefore.RowCount != _count)
                        throw new InvalidDataException("UI query workload changed catalog media rows.");
                    if ((int)typeof(ErrorReporter).GetProperty("ErrorCount", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)! != 0)
                        throw new InvalidOperationException("Application wrote an error report during the benchmark.");
                    using (var phase = BeginPhase("shutdown"))
                    {
                        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        window.Closed += (_, _) => closed.TrySetResult();
                        window.Close();
                        await closed.Task.WaitAsync(TimeSpan.FromSeconds(60));
                        phase.Complete();
                    }
                    exitCode = 0;
                }
                catch (Exception ex) { failure = ex.ToString(); }
                finally { app.Shutdown(exitCode); }
            }));
            app.Run();
            // All comparable measurements, integrity scans and memory samples are already complete.
            // A diagnostic failure is reported separately; it cannot rewrite their verdict or timings.
            if (exitCode == 0) DiagnoseFolderGroupsAsync(store).GetAwaiter().GetResult();
        }
        catch (Exception ex) { failure = ex.ToString(); }
        var fixtureRetained = true;
        string? cleanupError = null;
        if (exitCode == 0)
        {
            try
            {
                using var phase = BeginPhase("fixture-cleanup");
                // Only this atomically-created fixture is eligible. Failed runs remain available for diagnosis.
                var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (!LocalCatalogStore.IsIsolatedSmokeCatalog || LocalCatalogStore.CatalogDirectory != catalog ||
                    !string.Equals(Path.GetDirectoryName(catalog), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(catalog).StartsWith("PhotoShelf-ui-smoke-", StringComparison.Ordinal) ||
                    (File.GetAttributes(catalog) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Fixture cleanup ownership check failed; retained.");
                SqliteConnection.ClearAllPools();
                Directory.Delete(catalog, recursive: true);
                fixtureRetained = false;
                phase.Complete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { cleanupError = ex.Message; }
        }
        var result = new
        {
            schema = 1, status = exitCode == 0 ? "passed" : "failed", error = failure,
            revision = _revision, label = _label, applicationVersion = ErrorReporter.Version,
            count = _count, repetitions = _repetitions, warmups = 2, catalogRoot = catalog, fixtureRetained, cleanupError,
            fixture = "deterministic-v1-catalog-only", syntheticFileBytes = 0,
            setupProfile = SetupProfile, phaseTimings = Phases, postWorkloadFolderDiagnostics = FolderDiagnostics,
            scope = "SQLite queries and actual WPF event-to-bound-data-page/layout; absent synthetic media, decoding/indexing and physical input excluded; background writers quiesced",
            clock = "Stopwatch", clockFrequency = Stopwatch.Frequency, timestampUtc = DateTime.UtcNow,
            machine = new { name = Environment.MachineName, os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(), framework = RuntimeInformation.FrameworkDescription,
                logicalProcessors = Environment.ProcessorCount, totalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                runnerImage = Environment.GetEnvironmentVariable("ImageVersion"), runnerOs = Environment.GetEnvironmentVariable("RUNNER_OS"),
                dpi = "WPF default", viewport = "1280x800 device independent pixels" },
            confirmedSupersessions = _cancellations, supersessions = Supersessions, samples = Samples, memory = Memory,
            projectionTimings = ProjectionTimings,
            catalogDataIntegrity = new { before = catalogBefore, after = catalogAfter,
                unchanged = catalogBefore is not null && catalogBefore == catalogAfter,
                scope = "All desktop_media_items columns ordered by asset_id, before/after UI workload; settings and caches excluded; outside timing samples" }
        };
        JsonSerializer.Serialize(report, result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        report.Flush(true);
        Console.WriteLine($"{result.status}: {_reportPath}");
        return exitCode;
    }

    private static async Task SeedAsync(SqliteDesktopCatalogStore store)
    {
        using (var phase = BeginPhase("seed-initialize"))
        {
            await store.InitializeAsync();
            await store.SaveAsync(new LocalCatalogState { IncludeSystemFolders = true, DateGroupingMode = "FileDate" }, saveItems: false);
            phase.Complete();
        }
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(LocalCatalogStore.CatalogDirectory, "catalog-v2.sqlite"), Pooling = false }.ToString());
        await db.OpenAsync();
        using (var phase = BeginPhase("seed-configure"))
        {
            await using var setup = db.CreateCommand();
            // Fixture setup only: this unpooled connection closes before any query timing.
            // Do not change production connection settings, cache_spill, indexes or durability.
            setup.CommandText = $"PRAGMA synchronous=FULL; PRAGMA cache_size=-{SeedCacheKiB};";
            await setup.ExecuteNonQueryAsync();
            setup.CommandText = "PRAGMA cache_size;";
            if (Convert.ToInt64(await setup.ExecuteScalarAsync()) != -SeedCacheKiB)
                throw new InvalidDataException("Fixture cache profile was not applied.");
            setup.CommandText = "PRAGMA synchronous;";
            if (Convert.ToInt64(await setup.ExecuteScalarAsync()) != 2)
                throw new InvalidDataException("Fixture setup requires FULL durability.");
            setup.CommandText = "PRAGMA journal_mode;";
            if (!string.Equals(Convert.ToString(await setup.ExecuteScalarAsync()), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Fixture setup requires the production WAL mode.");
            phase.Complete();
        }
        await using var command = db.CreateCommand();
        // Bulk setup is deliberately outside measurements and does not benchmark production ingestion.
        // Populate only common columns: each revision retains its real schema, defaults, triggers and indexes.
        command.CommandText = """
            WITH digits(n) AS (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)), numbers(i) AS (
              SELECT a.n+10*b.n+100*c.n+1000*d.n+10000*e.n+100000*f.n
              FROM digits a,digits b,digits c,digits d,digits e,digits f
            ), fixture AS (
              SELECT i, $root || '\bucket' || printf('%02d',i%100) || '\' ||
                CASE WHEN i%97=0 THEN 'needle_' ELSE 'synthetic_' END || printf('%08d',i) || '.jpg' AS path,
                $ticks + (i%3650)*864000000000 AS date FROM numbers WHERE i<$count
            )
            INSERT INTO desktop_media_items(asset_id,path,path_key,folder_key,search_key,is_favorite,size_bytes,
                file_modified_utc_ticks,file_local_ticks,file_month,is_video,capture_date_ticks,capture_month,
                metadata_indexed,metadata_status,is_hidden_or_system,last_seen_utc)
            SELECT printf('%032x',i+1),path,upper(replace(path,'\','/')),
                upper(replace($root || '\bucket' || printf('%02d',i%100),'\','/')),upper(replace(path,'\','/')),
                i%13=0,1024+i%31,date,date,strftime('%Y-%m','2015-01-01',printf('+%d days',i%3650)),0,
                CASE WHEN i%7=0 THEN NULL ELSE date-864000000000 END,
                CASE WHEN i%7=0 THEN NULL ELSE strftime('%Y-%m','2015-01-01',printf('%+d days',i%3650-1)) END,
                1,CASE WHEN i%7=0 THEN 2 ELSE 1 END,0,'2026-09-28T00:00:00Z' FROM fixture;
            """;
        command.Parameters.AddWithValue("$root", _mediaRoot);
        command.Parameters.AddWithValue("$ticks", new DateTime(2015, 1, 1).Ticks);
        command.Parameters.AddWithValue("$count", _count);
        command.CommandTimeout = 600;
        using (var phase = BeginPhase("seed-insert"))
        { await command.ExecuteNonQueryAsync(); phase.Complete(); }
        using (var phase = BeginPhase("seed-checkpoint-and-close"))
        {
            await using var checkpoint = db.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await using (var reader = await checkpoint.ExecuteReaderAsync())
                if (!await reader.ReadAsync() || reader.GetInt64(0) != 0)
                    throw new InvalidDataException("Fixture WAL checkpoint was busy or incomplete.");
            await db.CloseAsync();
            phase.Complete();
        }
        using (var phase = BeginPhase("seed-verify-count"))
        {
            if (await store.CountAsync(new CatalogViewQuery { IncludeSystemFolders = true }) != _count)
                throw new InvalidDataException("Fixture count mismatch.");
            phase.Complete();
        }
    }

    private static async Task MeasureSqlAsync(SqliteDesktopCatalogStore store)
    {
        var query = new CatalogViewQuery { PageSize = 128, IncludeSystemFolders = true };
        var queries = new (string Name, CatalogViewQuery Query)[]
        {
            ("sql.page.first", query), ("sql.page.deep", query with { Offset = _count - 128 }),
            ("sql.page.capture", query with { UseCaptureDate = true }),
            ("sql.page.folder", query with { Folder = Path.Combine(_mediaRoot, "bucket00"), ViewMode = "Folder" }),
            ("sql.page.search", query with { SearchText = "needle_" })
        };
        foreach (var (name, current) in queries)
            await RepeatAsync(name, async () =>
            {
                var page = await store.QueryPageAsync(current);
                if (page.Items.Count != 128) throw new InvalidDataException($"{name}: incomplete data page.");
                return Fingerprint(page.Items.Select(x => x.Path));
            });
        foreach (var capture in new[] { false, true })
            await RepeatAsync(capture ? "sql.groups.capture" : "sql.groups.file", async () =>
            {
                var groups = await store.QueryGroupsAsync(query with { UseCaptureDate = capture });
                if (groups.Sum(x => x.Count) != _count) throw new InvalidDataException("Group count mismatch.");
                return string.Join(";", groups.Select(x => $"{x.Key}:{x.Count}"));
            });
        await RepeatAsync("sql.groups.search", async () =>
        {
            var groups = await store.QueryGroupsAsync(query with { SearchText = "needle_" });
            if (groups.Sum(x => x.Count) != (_count + 96) / 97) throw new InvalidDataException("Search group count mismatch.");
            return string.Join(";", groups.Select(x => $"{x.Key}:{x.Count}"));
        });
        // Query probes isolate anchor lookup cost; they do not pretend to reconstruct a baseline UI phase.
        var anchor = Path.Combine(_mediaRoot, "bucket00", "needle_00000000.jpg");
        foreach (var capture in new[] { false, true })
            await RepeatAsync(capture ? "sql.index_of.capture" : "sql.index_of.file", async () =>
            {
                var index = await store.IndexOfAsync(query with { UseCaptureDate = capture }, anchor);
                if (index is null || index < 0 || index >= _count) throw new InvalidDataException("Anchor lookup failed.");
                return index.Value.ToString(CultureInfo.InvariantCulture);
            });
    }

    private static async Task RepeatAsync(string name, Func<Task<string>> operation)
    {
        using var phase = BeginPhase(name);
        for (var i = -2; i < _repetitions; i++)
        {
            var watch = Stopwatch.StartNew(); var fingerprint = await operation();
            if (i >= 0) Samples.Add(new(name, i, watch.Elapsed.TotalMilliseconds, fingerprint));
        }
        phase.Complete();
    }

    private static async Task DiagnoseFolderGroupsAsync(SqliteDesktopCatalogStore store)
    {
        // Same queries on both revisions, after shutdown. No samples, memory snapshots or
        // performance gates consume these single diagnostic timings or query plans.
        foreach (var bucket in new[] { "bucket00", "bucket01" })
        {
            using var phase = BeginPhase("post-workload-folder." + bucket);
            var query = new CatalogViewQuery
            {
                Folder = Path.Combine(_mediaRoot, bucket), ViewMode = "Folder", IncludeSubfolders = true,
                IncludeSystemFolders = true, ShowVideos = true, UseCaptureDate = true, NewestFirst = false
            };
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var watch = Stopwatch.StartNew();
                var groups = await store.QueryGroupsAsync(query, timeout.Token);
                var elapsed = watch.Elapsed.TotalMilliseconds;
                if (groups.Sum(group => group.Count) != _count / 100)
                    throw new InvalidDataException("Diagnostic folder group count mismatch.");
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(LocalCatalogStore.CatalogDirectory, "catalog-v2.sqlite"),
                    Mode = SqliteOpenMode.ReadOnly, Pooling = false
                }.ToString());
                await connection.OpenAsync(timeout.Token);
                using var interrupt = timeout.Token.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
                await using var transaction = connection.BeginTransaction(deferred: true);
                var helper = typeof(SqliteDesktopCatalogStore).GetMethod("BuildGroupsCommandAsync", BindingFlags.Static | BindingFlags.NonPublic);
                await using var command = helper is not null
                    ? await (Task<SqliteCommand>)helper.Invoke(null, [query, connection, transaction, timeout.Token])!
                    : BuildBaselineGroupsCommand(query, connection, transaction, timeout.Token);
                var sql = command.CommandText;
                var parameters = command.Parameters.Cast<SqliteParameter>().ToDictionary(parameter => parameter.ParameterName, parameter => parameter.Value);
                command.CommandText = "EXPLAIN QUERY PLAN " + sql;
                var plan = new List<object>();
                await using (var reader = await command.ExecuteReaderAsync(timeout.Token))
                    while (await reader.ReadAsync(timeout.Token))
                        plan.Add(new { id = reader.GetInt64(0), parent = reader.GetInt64(1), detail = reader.GetString(3) });
                FolderDiagnostics.Add(new
                {
                    status = "passed", folder = bucket, query.UseCaptureDate, query.NewestFirst, query.IncludeSubfolders,
                    query.SearchText, query.IncludeSystemFolders, query.ShowVideos, expectedItems = _count / 100,
                    groupCount = groups.Count, itemCount = groups.Sum(group => group.Count), milliseconds = elapsed,
                    fingerprint = string.Join(";", groups.Select(group => $"{group.Key}:{group.Count}")),
                    commandBuilder = helper is null ? "baseline-BuildFrom-BuildGroupFilter" : "production-BuildGroupsCommandAsync",
                    sql, parameters, explainQueryPlan = plan,
                    scope = "Single post-workload diagnostic; not a comparable sample or performance gate. EXPLAIN uses a separate read-only snapshot."
                });
                phase.Complete();
            }
            catch (Exception ex)
            {
                // Keep diagnostics visibly incomplete, rather than fabricating a query plan or
                // making an optional reflection hook alter already completed benchmark samples.
                FolderDiagnostics.Add(new { status = "failed", folder = bucket, error = ex.ToString() });
            }
        }
    }

    private static SqliteCommand BuildBaselineGroupsCommand(CatalogViewQuery query, SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken token)
    {
        var type = typeof(SqliteDesktopCatalogStore);
        var buildFrom = type.GetMethod("BuildFrom", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(type.FullName, "BuildFrom");
        var groupFilter = type.GetMethod("BuildGroupFilter", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(type.FullName, "BuildGroupFilter");
        var command = connection.CreateCommand(); command.Transaction = transaction;
        try
        {
            // Exact v0.10.8 QueryGroupsAsync shape; both predicates and parameters come
            // from that unmodified revision's production builders, never a test filter.
            var month = query.UseCaptureDate ? "capture_month" : "file_month";
            var from = (string)buildFrom.Invoke(null, [query, command, token])!;
            var filter = (string)groupFilter.Invoke(null, [query, command])!;
            command.CommandText = $"SELECT {month},COUNT(*) FROM {from} WHERE {filter} GROUP BY {month} ORDER BY {month} {(query.NewestFirst ? "DESC" : "ASC")};";
            return command;
        }
        catch { command.Dispose(); throw; }
    }

    private static async Task MeasureUiAsync(MainWindow window)
    {
        var date = (ComboBox)window.FindName("DateModeBox");
        var search = (TextBox)window.FindName("SearchBox");
        var sort = (Button)window.FindName("DateSortButton");
        await MeasureActionAsync(window, "ui.date", () => date.SelectedIndex = date.SelectedIndex == 0 ? 1 : 0,
            q => q.UseCaptureDate == (date.SelectedIndex == 0));
        var expectedNewestFirst = Get<CatalogViewQuery>(window, "_currentQuery").NewestFirst;
        await MeasureActionAsync(window, "ui.sort", () =>
        {
            expectedNewestFirst = !Get<CatalogViewQuery>(window, "_currentQuery").NewestFirst;
            sort.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }, query => query.NewestFirst == expectedNewestFirst);
        await MeasureActionAsync(window, "ui.search", () => search.Text = search.Text == "needle_" ? "synthetic_" : "needle_",
            q => q.SearchText == search.Text);
        using (var phase = BeginPhase("ui.clear-search"))
        { search.Text = ""; await WaitForIdleAsync(window); phase.Complete(); }
        var bucket = 0;
        await MeasureActionAsync(window, "ui.folder", () =>
        {
            bucket = 1 - bucket;
            var node = new FolderNode(Path.Combine(_mediaRoot, $"bucket{bucket:00}")) { ChildrenLoaded = true };
            Invoke(window, "OnFolderTreeSelectedItemChanged", window.FindName("FolderTree"), new RoutedPropertyChangedEventArgs<object>(null!, node));
        }, q => q.Folder == Path.Combine(_mediaRoot, $"bucket{bucket:00}"));
        using var supersessionPhase = BeginPhase("ui.supersessions");
        Invoke(window, "OnAllPhotosClicked", window, new RoutedEventArgs());
        await WaitForIdleAsync(window);
        for (var repeat = 0; repeat < 5; repeat++)
        {
            // Force an old pending projection, capture its token before it can be disposed, then supersede it.
            search.Text = "superseded_" + repeat;
            var oldToken = Get<CancellationTokenSource?>(window, "_projectionCancellation")?.Token
                ?? throw new InvalidOperationException("Projection did not expose an active cancellation token.");
            var cancellationWatch = Stopwatch.StartNew();
            search.Text = "needle_";
            if (!oldToken.IsCancellationRequested) throw new InvalidOperationException("Superseded request was not canceled.");
            var signalMs = cancellationWatch.Elapsed.TotalMilliseconds;
            _cancellations++;
            await WaitForIdleAsync(window);
            if (Get<CatalogViewQuery>(window, "_currentQuery").SearchText != "needle_" ||
                window.PhotoRows is not VirtualPhotoRows rows || rows.ItemCount != (_count + 96) / 97 ||
                rows.LoadedItems.Any(x => !Path.GetFileName(x.Path).StartsWith("needle_", StringComparison.Ordinal)))
                throw new InvalidOperationException("Stale request replaced the latest search.");
            Supersessions.Add(new { iteration = repeat, cancellationSignalMs = signalMs,
                finalSettledPageMs = cancellationWatch.Elapsed.TotalMilliseconds,
                scope = "pending projection superseded before debounce; not a native SQL interruption benchmark" });
            await Task.Delay(100); await WaitForIdleAsync(window);
            if (Get<CatalogViewQuery>(window, "_currentQuery").SearchText != "needle_")
                throw new InvalidOperationException("Stale results appeared after final publication.");
        }
        SnapshotMemory(window, "end");
        supersessionPhase.Complete();
    }

    private static async Task MeasureActionAsync(MainWindow window, string name, Action action, Func<CatalogViewQuery, bool> expected)
    {
        using var phase = BeginPhase(name);
        for (var i = -2; i < _repetitions; i++)
        {
            await QuiesceAsync(window); await WaitForIdleAsync(window);
            var oldRows = window.PhotoRows;
            var published = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            var watch = Stopwatch.StartNew();
            void Observe(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(MainWindow.PhotoRows) && !ReferenceEquals(oldRows, window.PhotoRows) &&
                    window.PhotoRows is VirtualPhotoRows rows && rows.ItemCount > 0 && rows.LoadedItems.Any() &&
                    expected(Get<CatalogViewQuery>(window, "_currentQuery"))) published.TrySetResult(watch.Elapsed.TotalMilliseconds);
            }
            window.PropertyChanged += Observe;
            try
            {
                action();
                // An actual dispatcher input-priority turn after the control handler, not timer tick spacing.
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                var ack = watch.Elapsed.TotalMilliseconds;
                var data = await published.Task.WaitAsync(TimeSpan.FromSeconds(60));
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                var layout = watch.Elapsed.TotalMilliseconds;
                if (i >= 0)
                {
                    var fingerprint = Fingerprint(((VirtualPhotoRows)window.PhotoRows).LoadedItems.Select(x => x.Path));
                    Samples.Add(new(name + ".ack", i, ack, ""));
                    Samples.Add(new(name + ".first_data_page", i, data, ""));
                    Samples.Add(new(name + ".layout", i, layout, fingerprint));
                    SnapshotMemory(window, name);
                }
                // Main timings are already captured. Baseline has no stage hook, and remains untouched.
                await WaitForIdleAsync(window);
                if (i >= 0) ProjectionTimings.Add(ReadProjectionTiming(window, name, i));
            }
            finally { window.PropertyChanged -= Observe; }
        }
        phase.Complete();
    }

    private static async Task QuiesceAsync(MainWindow window) =>
        await ((Task)Invoke(window, "StopCatalogWritersAsync", true)!).WaitAsync(TimeSpan.FromSeconds(120));

    private static async Task WaitForIdleAsync(MainWindow window)
    {
        var watch = Stopwatch.StartNew();
        while (Get<bool>(window, "_isProjecting") || Get<bool>(window, "_projectionQueued"))
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(120)) throw new TimeoutException("Projection did not settle.");
            await Task.Delay(10);
        }
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    }

    private static void SnapshotMemory(MainWindow window, string phase)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var rows = window.PhotoRows as VirtualPhotoRows;
        Memory.Add(new(phase, process.WorkingSet64, process.PrivateMemorySize64, GC.GetTotalMemory(false),
            rows?.CachedPageCount ?? 0, rows?.CachedRowCount ?? 0, rows?.PendingPageCount ?? 0));
    }

    private static ProjectionTimingSample ReadProjectionTiming(MainWindow window, string scenario, int iteration)
    {
        var field = typeof(MainWindow).GetField("_lastProjectionTiming", PrivateInstance);
        if (field is null) return new(scenario, iteration, false, null, null, null);
        var value = field.GetValue(window) ?? throw new InvalidOperationException("Projection stage hook exists but has no completed operation.");
        object Read(string property) => value.GetType().GetProperty(property)?.GetValue(value)
            ?? throw new InvalidOperationException($"Projection stage hook is missing {property}.");
        var durations = new Dictionary<string, double>();
        foreach (var name in new[] { "PreparationMs", "DebounceMs", "GroupsMs", "AnchorMs", "PrimeMs", "PublishMs", "TotalMs" })
        {
            var duration = Convert.ToDouble(Read(name), CultureInfo.InvariantCulture);
            if (!double.IsFinite(duration) || duration < 0) throw new InvalidDataException($"Invalid projection timing {name}.");
            durations.Add(name, duration);
        }
        return new(scenario, iteration, true, Convert.ToInt64(Read("OperationId"), CultureInfo.InvariantCulture),
            (string)Read("Outcome"), durations);
    }

    private static Task<CatalogSnapshot> SnapshotCatalogAsync() => Task.Run(async () =>
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(LocalCatalogStore.CatalogDirectory, "catalog-v2.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM desktop_media_items ORDER BY asset_id;"; command.CommandTimeout = 120;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(4096); long count = 0;
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            for (var column = 0; column < reader.FieldCount; column++) AppendCatalogValue(hash, reader.GetName(column), buffer);
            while (await reader.ReadAsync())
            {
                count++;
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    var value = reader.GetValue(column);
                    var text = value switch
                    {
                        DBNull => null,
                        string s => "text:" + s,
                        long n => "integer:" + n.ToString(CultureInfo.InvariantCulture),
                        double d => "real:" + d.ToString("R", CultureInfo.InvariantCulture),
                        _ => throw new InvalidDataException("Unsupported media-row value in read-only fingerprint.")
                    };
                    AppendCatalogValue(hash, text, buffer);
                }
            }
            return new CatalogSnapshot(count, Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    });

    private static void AppendCatalogValue(IncrementalHash hash, string? value, byte[] buffer)
    {
        var size = value is null ? -1 : Encoding.UTF8.GetByteCount(value);
        Span<byte> prefix = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, size); hash.AppendData(prefix);
        if (value is null) return;
        var rented = size > buffer.Length ? ArrayPool<byte>.Shared.Rent(size) : null;
        try
        {
            var target = rented ?? buffer; var written = Encoding.UTF8.GetBytes(value.AsSpan(), target);
            hash.AppendData(target.AsSpan(0, written));
        }
        finally { if (rented is not null) ArrayPool<byte>.Shared.Return(rented, clearArray: true); }
    }

    private static string Fingerprint(IEnumerable<string> paths) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", paths.Select(x => Path.GetRelativePath(_mediaRoot, x))))));
    private static PhaseScope BeginPhase(string name) => new(name);

    private sealed class PhaseScope : IDisposable
    {
        private readonly string _name;
        private readonly DateTime _startedUtc;
        private readonly Stopwatch _watch;
        private bool _completed;
        public PhaseScope(string name)
        {
            _name = name; _startedUtc = DateTime.UtcNow;
            Console.WriteLine($"{_startedUtc:O} {_label} {_count} BEGIN {name}");
            Console.Out.Flush();
            _watch = Stopwatch.StartNew();
        }
        public void Complete() => _completed = true;
        public void Dispose()
        {
            var elapsed = _watch.Elapsed.TotalMilliseconds;
            var outcome = _completed ? "completed" : "interrupted";
            Phases.Add(new(_name, _startedUtc, elapsed, outcome));
            Console.WriteLine(FormattableString.Invariant($"{DateTime.UtcNow:O} {_label} {_count} END {_name} {outcome} {elapsed:F1} ms"));
            Console.Out.Flush();
        }
    }
    private static T Get<T>(object target, string name) => (T)(typeof(MainWindow).GetField(name, PrivateInstance)
        ?? throw new MissingFieldException(typeof(MainWindow).FullName, name)).GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) =>
        (typeof(MainWindow).GetMethod(name, PrivateInstance) ?? throw new MissingMethodException(typeof(MainWindow).FullName, name)).Invoke(target, args);
    private sealed record Sample(string Name, int Iteration, double Milliseconds, string Fingerprint);
    private sealed record PhaseTiming(string Name, DateTime StartedUtc, double Milliseconds, string Outcome);
    private sealed record CatalogSnapshot(long RowCount, string Sha256);
    private sealed record ProjectionTimingSample(string Scenario, int Iteration, bool Available, long? OperationId,
        string? Outcome, IReadOnlyDictionary<string, double>? Durations);
    private sealed record MemorySample(string Phase, long WorkingSetBytes, long PrivateBytes, long ManagedBytes, int CachedPages, int CachedRows, int PendingPages);
}
