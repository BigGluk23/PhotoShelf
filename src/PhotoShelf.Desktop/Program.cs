using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using PhotoShelf.Application.Diagnostics;

namespace PhotoShelf.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var check = args.Contains("--verify-startup-resources", StringComparer.Ordinal);
        try
        {
            var app = new App(check);
            // Includes merged theme dictionaries; failures here preceded OnStartup in the old entry point.
            app.InitializeComponent();
            if (check) return VerifyStartupResources(app, args);
            return app.Run();
        }
        catch (Exception exception)
        {
            if (check)
            {
                var details = ExceptionDiagnostics.Create(exception, "Проверка встроенных ресурсов запуска", ErrorReporter.Version);
                ErrorReporter.Save(details.Details);
            }
            else ErrorReporter.Show(exception, "Не удалось запустить PhotoShelf");
            return 1;
        }
    }

    private static int VerifyStartupResources(App app, string[] args)
    {
        var reportIndex = Array.IndexOf(args, "--startup-report");
        if (reportIndex < 0 || reportIndex + 1 >= args.Length)
            throw new ArgumentException("Проверка требует --startup-report <новый файл отчёта>");
        var reportPath = Path.GetFullPath(args[reportIndex + 1]);
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
        if (app.VerificationFailed) throw new InvalidOperationException("Проверка запуска завершилась ошибкой Dispatcher.");
        using (var file = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            JsonSerializer.Serialize(file, new { status = "passed", check = "startup-resources", version = ErrorReporter.Version, resources = decoded });
            file.Flush(true);
        }
        app.Shutdown(0);
        return 0;
    }
}
