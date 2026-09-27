using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PhotoShelf.Desktop;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            var logPath = WriteCrashLog(args.Exception);
            System.Windows.MessageBox.Show(
                $"PhotoShelf поймал ошибку и не будет молча закрываться.\n\n{args.Exception.Message}\n\nПодробности:\n{logPath}",
                "Ошибка PhotoShelf",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };

        var loading = new LoadingWindow();
        loading.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        MainWindow mainWindow;
        try
        {
            var state = await PhotoShelf.Desktop.MainWindow.LoadInitialCatalogStateAsync();
            mainWindow = new MainWindow(state);
        }
        catch (Exception ex)
        {
            var logPath = WriteCrashLog(ex);
            System.Windows.MessageBox.Show(
                $"Не удалось открыть каталог. Данные сохранены.\n\n{ex.Message}\n\nПодробности:\n{logPath}",
                "PhotoShelf",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        MainWindow = mainWindow;
        mainWindow.Show();
        loading.Close();
    }

    private static string WriteCrashLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PhotoShelf",
                "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, exception.ToString());
            return path;
        }
        catch
        {
            return "лог не удалось записать";
        }
    }
}
