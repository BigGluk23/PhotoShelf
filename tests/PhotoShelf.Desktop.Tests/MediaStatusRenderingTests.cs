using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoShelf.Desktop;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

public sealed class MediaStatusRenderingTests
{
    [Fact]
    public Task EmptyStatusLeavesPhotoPixelsUnchangedAndNoticesStillAppear() => OnStaAsync(async () =>
    {
        RuntimeHelpers.RunClassConstructor(typeof(System.Windows.Application).TypeHandle);
        var resources = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PhotoShelf;component/Themes/PhotoShelfTheme.xaml")
        };
        var image = new PreviewImage
        {
            Source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 180, 50, 240, 255 }, 4),
            Stretch = Stretch.Fill
        };
        var status = new TextBlock
        {
            Background = new SolidColorBrush(Color.FromArgb(187, 32, 33, 38)),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8),
            Padding = new Thickness(8)
        };
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(PreviewImage.Status)) { Source = image });
        var surface = new Grid();
        surface.Children.Add(image);
        surface.Children.Add(status);

        status.Visibility = Visibility.Collapsed;
        var unobstructed = Render(surface);
        status.ClearValue(UIElement.VisibilityProperty);
        // Recreate the reported pre-fix behavior: an empty padded message paints over photo pixels.
        Assert.False(unobstructed.SequenceEqual(Render(surface)));

        status.Style = (Style)resources["MediaStatusText"];
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(Visibility.Collapsed, status.Visibility);
        Assert.Equal(unobstructed, Render(surface));

        foreach (var notice in new[] { "Нет встроенного декодера или файл недоступен", "Показан первый кадр" })
        {
            AsyncMediaImage.SetStatus(image, notice);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(notice, status.Text);
            Assert.Equal(Visibility.Visible, status.Visibility);
            Assert.False(unobstructed.SequenceEqual(Render(surface)));

            AsyncMediaImage.SetStatus(image, "");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Collapsed, status.Visibility);
            Assert.Equal(unobstructed, Render(surface));
        }

        image.SetValue(PreviewImage.StatusProperty, null);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(Visibility.Collapsed, status.Visibility);
        Assert.Equal(unobstructed, Render(surface));
    });

    private static byte[] Render(Grid surface)
    {
        surface.Measure(new Size(320, 180));
        surface.Arrange(new Rect(0, 0, 320, 180));
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixels = new byte[320 * 180 * 4];
        bitmap.CopyPixels(pixels, 320 * 4, 0);
        return pixels;
    }

    private static Task OnStaAsync(Func<Task> body)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await body(); completed.TrySetResult(); }
                catch (Exception exception) { completed.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
