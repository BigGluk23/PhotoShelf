using System.Windows;
using System.Windows.Threading;
using System.IO;
using PhotoShelf.Infrastructure.Sqlite;
using PhotoShelf.Application.Diagnostics;

namespace PhotoShelf.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private FileStream? _catalogLease;
    private bool _startupCompleted;
    private readonly bool _verificationOnly;
    internal bool VerificationFailed { get; private set; }

    public App() : this(false) { }

    public App(bool verificationOnly)
    {
        _verificationOnly = verificationOnly;
        DispatcherUnhandledException += OnUnhandledDispatcherException;
    }

    private void OnUnhandledDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        args.Handled = true;
        if (_verificationOnly)
        {
            VerificationFailed = true;
            ErrorReporter.Save(ExceptionDiagnostics.Create(args.Exception, "Проверка встроенных ресурсов запуска", ErrorReporter.Version).Details);
            Shutdown(1);
            return;
        }
        ErrorReporter.Show(args.Exception, _startupCompleted
            ? "Не удалось выполнить действие в PhotoShelf"
            : "Не удалось подготовить окно PhotoShelf");
        if (!_startupCompleted) Shutdown(1);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Application queues OnStartup before Run; a nested dispatcher pump must remain harmless.
        if (_verificationOnly) return;
        try
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _instanceMutex = new Mutex(true, "Local\\PhotoShelf.Catalog.SingleWriter", out var firstInstance);
            if (!firstInstance)
            {
                System.Windows.MessageBox.Show("PhotoShelf уже запущен. Откройте его окно через значок в области уведомлений.", "PhotoShelf");
                _instanceMutex.Dispose(); _instanceMutex = null; Shutdown(); return;
            }
            // The handle protects this catalog across Windows login sessions too.
            Directory.CreateDirectory(LocalCatalogStore.CatalogDirectory);
            _catalogLease = new FileStream(Path.Combine(LocalCatalogStore.CatalogDirectory, "writer.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            // Keep every stage inside the same error boundary, including the splash XAML and Show().
            var loading = new LoadingWindow();
            loading.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (Dispatcher.HasShutdownStarted) return;
            var state = await PhotoShelf.Desktop.MainWindow.LoadInitialCatalogStateAsync();
            if (Dispatcher.HasShutdownStarted) return;
            var mainWindow = new MainWindow(state);
            MainWindow = mainWindow;
            mainWindow.Show();
            _startupCompleted = true;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            loading.Close();
        }
        catch (Exception exception)
        {
            ErrorReporter.Show(exception, "Не удалось запустить PhotoShelf");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _catalogLease?.Dispose();
        if (_instanceMutex is not null) { _instanceMutex.ReleaseMutex(); _instanceMutex.Dispose(); }
        base.OnExit(e);
    }
}
