using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using PhotoShelf.Application.Diagnostics;

namespace PhotoShelf.Desktop;

public static class ErrorReporter
{
    public static string Version => typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static void Show(Exception exception, string context)
    {
        var report = ExceptionDiagnostics.Create(exception, context, Version);
        var path = Save(report.Details);
        var logMessage = path is null
            ? "Не удалось записать журнал. Сохраните текст причины из этого окна."
            : $"Подробный журнал:\n{path}";
        System.Windows.MessageBox.Show($"{context}\n\nПричина: {report.RootCause}\n\n{logMessage}",
            "Ошибка PhotoShelf", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public static string? Save(string details)
    {
        var directories = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoShelf", "diagnostics", "errors"),
            Path.Combine(Path.GetTempPath(), "PhotoShelf", "diagnostics", "errors")
        };
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
