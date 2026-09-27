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
            System.Windows.MessageBox.Show(
                $"PhotoShelf поймал ошибку и не будет молча закрываться.\n\n{args.Exception.Message}",
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
            System.Windows.MessageBox.Show($"Не удалось открыть каталог. Данные сохранены.\n{ex.Message}", "PhotoShelf");
            Shutdown(1);
            return;
        }
        MainWindow = mainWindow;
        mainWindow.Show();
        loading.Close();
    }
}
