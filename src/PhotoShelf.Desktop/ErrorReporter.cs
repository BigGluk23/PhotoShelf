using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using PhotoShelf.Application.Diagnostics;
using PhotoShelf.Infrastructure.Sqlite;

namespace PhotoShelf.Desktop;

public static class ErrorReporter
{
    private static int _errorCount;
    internal static bool AutomatedCheck { get; set; }
    internal static int ErrorCount => Volatile.Read(ref _errorCount);
    internal static string LogRoot => Path.Combine(LocalCatalogStore.DerivedDataDirectory, "diagnostics", "errors");
    public static string Version => typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static void Show(Exception exception, string context)
    {
        var report = ExceptionDiagnostics.Create(exception, context, Version);
        var path = Save(report.Details);
        if (AutomatedCheck) return;
        var logMessage = path is null
            ? "Не удалось записать журнал. Сохраните текст причины из этого окна."
            : $"Подробный журнал:\n{path}";
        System.Windows.MessageBox.Show($"{context}\n\nПричина: {report.RootCause}\n\n{logMessage}",
            "Ошибка PhotoShelf", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public static string? Save(string details)
    {
        Interlocked.Increment(ref _errorCount);
        // Automated checks must never leak diagnostics into the real user's catalog or another run.
        var directories = AutomatedCheck
            ? new[] { LogRoot }
            : new[] { LogRoot, Path.Combine(Path.GetTempPath(), "PhotoShelf", "diagnostics", "errors") };
        foreach (var directory in directories)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"error-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.log");
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                var bytes = Encoding.UTF8.GetBytes(details);
                file.Write(bytes); file.Flush(true);
                return path;
            }
            catch (Exception) { /* Reporting must preserve the original error if logging is unavailable. */ }
        }
        return null;
    }
}
