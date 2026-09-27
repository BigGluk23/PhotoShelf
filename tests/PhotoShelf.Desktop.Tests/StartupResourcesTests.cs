using System.Collections;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using System.Windows.Threading;
using PhotoShelf.Desktop;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

// These tests execute WPF on Windows. Cross-compiling them does not validate startup.
// TestAssembly disables parallel execution because the current directory is process-wide.
public sealed class StartupResourcesTests
{
    [Fact]
    public void StartupAssetsAreWpfResourcesRatherThanLooseContent()
    {
        var assembly = typeof(LoadingWindow).Assembly;
        Assert.DoesNotContain(assembly.GetCustomAttributes<AssemblyAssociatedContentFileAttribute>(),
            attribute => attribute.RelativeContentFilePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase));

        using var stream = assembly.GetManifestResourceStream("PhotoShelf.g.resources");
        Assert.NotNull(stream);
        using var reader = new ResourceReader(stream);
        var names = reader.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("assets/photoshelf.ico", names);
        Assert.Contains("assets/giraffe-icon.png", names);
    }

    [Fact]
    public Task BothPackUrisDecodeFromAnEmptyWorkingDirectory() => OnStaAsync(() =>
    {
        using var directory = new EmptyWorkingDirectory();
        var icon = BitmapDecoder.Create(new Uri(AppResources.IconUri, UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var giraffe = BitmapDecoder.Create(new Uri(AppResources.GiraffeUri, UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        Assert.NotEmpty(icon.Frames);
        Assert.NotEmpty(giraffe.Frames);
        Assert.True(icon.Frames[0].PixelWidth > 0);
        Assert.True(giraffe.Frames[0].PixelWidth > 0);
        Assert.False(Directory.Exists(Path.Combine(Environment.CurrentDirectory, "Assets")));
    });

    [Fact]
    public Task LoadingWindowInitializesItsIconAndImageWithoutLooseAssets() => OnStaAsync(() =>
    {
        using var directory = new EmptyWorkingDirectory();
        var window = new LoadingWindow();
        try
        {
            var icon = Assert.IsAssignableFrom<BitmapSource>(window.Icon);
            Assert.True(icon.PixelWidth > 0);
            var border = Assert.IsType<Border>(window.Content);
            var grid = Assert.IsType<Grid>(border.Child);
            var image = Assert.Single(grid.Children.OfType<Image>());
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.Source);
            Assert.True(bitmap.PixelWidth > 0);
            Assert.True(bitmap.PixelHeight > 0);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task TrayIconRemainsUsableAfterResourceStreamHasBeenClosed() => OnStaAsync(() =>
    {
        using var directory = new EmptyWorkingDirectory();
        using (var stream = AppResources.OpenIconStream())
        {
            Assert.True(stream.CanRead);
            Assert.Equal(0, stream.ReadByte());
            Assert.Equal(0, stream.ReadByte());
            Assert.Equal(1, stream.ReadByte());
            Assert.Equal(0, stream.ReadByte());
        }

        // CreateTrayIcon owns and closes its input stream before returning the cloned icon.
        using var icon = AppResources.CreateTrayIcon();
        using var bitmap = icon.ToBitmap();
        Assert.True(bitmap.Width > 0);
        Assert.True(bitmap.Height > 0);
        using var saved = new MemoryStream();
        icon.Save(saved);
        Assert.True(saved.Length > 4);
    });

    private static Task OnStaAsync(Action body)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // Register WPF pack URI handling without constructing the process-wide Application.
                    RuntimeHelpers.RunClassConstructor(typeof(System.Windows.Application).TypeHandle);
                    body();
                    completed.TrySetResult();
                }
                catch (Exception exception) { completed.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class EmptyWorkingDirectory : IDisposable
    {
        private readonly string _previous = Environment.CurrentDirectory;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "photoshelf-startup-resource-test-" + Guid.NewGuid().ToString("N"));

        public EmptyWorkingDirectory()
        {
            Directory.CreateDirectory(_directory);
            Environment.CurrentDirectory = _directory;
        }

        public void Dispose()
        {
            Environment.CurrentDirectory = _previous;
            Directory.Delete(_directory); // Only our empty fixture directory; never application or user assets.
        }
    }
}
