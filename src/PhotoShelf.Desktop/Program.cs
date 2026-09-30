using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using PhotoShelf.Application.Diagnostics;
using PhotoShelf.Infrastructure.Sqlite;
using System.Diagnostics;
using PhotoShelf.Application.Updates;
using PhotoShelf.Application.Updates.Installation;

namespace PhotoShelf.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Old shortcuts keep opening the version explicitly activated by the updater.
        // Diagnostic entry points always remain isolated and never follow a user's installation.
        if (args.Length == 0)
        {
            try
            {
                var active = ActiveInstallationResolver.Resolve(new UpdateInstallationPaths(), UpdateTrust.PublicKeyPem);
                if (active is not null && UpdateVersion.IsNewer(active.Version, MainWindow.RunningUpdateVersion))
                {
                    using var process = Process.Start(new ProcessStartInfo(active.ExecutablePath)
                    { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(active.ExecutablePath)! })
                        ?? throw new IOException("Не удалось запустить установленную версию PhotoShelf.");
                    return 0;
                }
            }
            catch (Exception exception)
            {
                ErrorReporter.Show(exception, "Не удалось проверить установленное обновление. Каталог не был открыт");
                return 1;
            }
        }
        StartupCheckArguments options;
        FileStream? report = null;
        try
        {
            options = StartupCheckArguments.Parse(args);
            if (options.ReportPath is not null)
                // Reserve exactly one new output. Never overwrite an existing report or arbitrary file.
                report = new FileStream(options.ReportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2; // No Application or catalog has been initialized for malformed arguments.
        }

        using (report)
        {
            var automated = options.Kind != StartupCheckKind.None;
            var uiSmoke = options.Kind == StartupCheckKind.UserInterface ? new UiSmokeSession() : null;
            var uiBrowseSmoke = options.Kind == StartupCheckKind.UserInterfaceBrowse ? new UiBrowseSmokeSession() : null;
            try
            {
                if (automated)
                {
                    // This must precede App construction, theme parsing, and every static media cache.
                    LocalCatalogStore.CreateIsolatedSmokeCatalog();
                    ErrorReporter.AutomatedCheck = true;
                }
                var app = new App(options.Kind == StartupCheckKind.Resources, uiSmoke, uiBrowseSmoke);
                // Includes merged theme dictionaries; failures here preceded OnStartup in the old entry point.
                app.InitializeComponent();
                if (options.Kind == StartupCheckKind.Resources)
                {
                    var result = VerifyStartupResources(app);
                    WriteReport(report!, result);
                    return 0;
                }
                var exitCode = app.Run();
                if (uiBrowseSmoke is not null)
                {
                    if (!uiBrowseSmoke.ChecksPassed || !uiBrowseSmoke.GracefulExit || ErrorReporter.ErrorCount != 0) exitCode = 1;
                    WriteReport(report!, uiBrowseSmoke.CreateReport(exitCode));
                    return exitCode;
                }
                if (uiSmoke is null) return exitCode;
                if (!uiSmoke.Ready || !uiSmoke.PreviewRendered || !uiSmoke.GracefulExit ||
                    uiSmoke.DispatcherTicks < 20 || uiSmoke.MaxDispatcherGapMs > 2000 || ErrorReporter.ErrorCount != 0) exitCode = 1;
                WriteReport(report!, uiSmoke.CreateReport(exitCode));
                return exitCode;
            }
            catch (Exception exception)
            {
                if (!automated)
                    ErrorReporter.Show(exception, "Не удалось запустить PhotoShelf");
                else
                {
                    // If temporary-root creation itself failed, do not fall back to the real catalog.
                    if (LocalCatalogStore.IsIsolatedSmokeCatalog)
                        ErrorReporter.Save(ExceptionDiagnostics.Create(exception, "Автоматическая проверка запуска", ErrorReporter.Version).Details);
                    try
                    {
                        WriteReport(report!, uiBrowseSmoke is not null && LocalCatalogStore.IsIsolatedSmokeCatalog
                            ? uiBrowseSmoke.CreateReport(1)
                            : uiSmoke is not null && LocalCatalogStore.IsIsolatedSmokeCatalog
                            ? uiSmoke.CreateReport(1)
                            : new
                            {
                                status = "failed", check = options.Kind == StartupCheckKind.Resources ? "startup-resources" :
                                    options.Kind == StartupCheckKind.UserInterfaceBrowse ? "ui-browse-smoke" : "ui-smoke",
                                error = exception.Message,
                                catalogRoot = LocalCatalogStore.IsIsolatedSmokeCatalog ? LocalCatalogStore.CatalogDirectory : null,
                                logRoot = LocalCatalogStore.IsIsolatedSmokeCatalog ? ErrorReporter.LogRoot : null
                            });
                    }
                    catch (Exception) { /* Nonzero exit plus missing/invalid report is a harness failure. */ }
                }
                return 1;
            }
        }
    }

    private static void WriteReport(FileStream report, object result)
    {
        report.Position = 0;
        report.SetLength(0); // This stream exclusively owns a file created by this invocation.
        JsonSerializer.Serialize(report, result, new JsonSerializerOptions { WriteIndented = true });
        report.Flush(true);
    }

    private static object VerifyStartupResources(App app)
    {
        // The App verification-only guard also blocks queued/reentrant OnStartup from opening the catalog.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var decoded = new List<object>();
        foreach (var uri in new[] { AppResources.IconUri, AppResources.GiraffeUri })
        {
            using var stream = AppResources.Open(uri);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            decoded.Add(new { uri, frames = decoder.Frames.Count, width = decoder.Frames[0].PixelWidth, height = decoder.Frames[0].PixelHeight });
        }
        using var tray = AppResources.CreateTrayIcon();
        var loading = new LoadingWindow();
        try { loading.Show(); loading.UpdateLayout(); }
        finally { loading.Close(); }
        if (app.VerificationFailed || ErrorReporter.ErrorCount != 0)
            throw new InvalidOperationException("Проверка запуска завершилась ошибкой Dispatcher.");
        app.Shutdown(0);
        return new
        {
            status = "passed", check = "startup-resources", version = ErrorReporter.Version,
            resources = decoded, catalogRoot = LocalCatalogStore.CatalogDirectory, logRoot = ErrorReporter.LogRoot
        };
    }
}
