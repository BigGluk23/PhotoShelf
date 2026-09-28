using System.Windows;
using System.Windows.Threading;

namespace PhotoShelf.Desktop.Tests;

/// <summary>
/// The real application has one UI dispatcher. Tests that load previews or pause their
/// readers must use that same dispatcher too: AsyncMediaImage verifies Application.Current
/// ownership and its registered images. A fresh STA per test is insufficient once an
/// Application exists in this test process.
/// </summary>
internal static class WpfTestDispatcher
{
    private static readonly Lazy<Task<Dispatcher>> Host = new(Start);

    public static async Task RunAsync(Func<Task> body)
    {
        var dispatcher = await Host.Value.WaitAsync(TimeSpan.FromSeconds(15));
        await dispatcher.InvokeAsync(body).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static Task<Dispatcher> Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // Plain WPF Application never runs PhotoShelf startup or opens a user catalog.
                // WPF allows one Application per AppDomain, so all participating test classes
                // share this host and close only their own windows, never the application.
                var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/PhotoShelf;component/Themes/PhotoShelfTheme.xaml") });
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                ready.TrySetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        }) { IsBackground = true, Name = "PhotoShelf shared WPF test dispatcher" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }
}
