using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Xunit;

namespace PhotoShelf.Desktop.Tests;

public sealed class BackgroundOperationsTests
{
    [Fact]
    public Task BackgroundActivityIsVisibleWithoutMoveHistoryAndPauseWaitsForDrain() => WpfTestDispatcher.RunAsync(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("photoshelf-activity-ui-").FullName;
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseCalled = false;
        var activity = new BackgroundActivitySnapshot("Выполняются фоновые задачи", "Метаданные: 17; осталось: 42; текущий файл: fixture.png", false, true);
        var window = new OperationsWindow(directory, (_, _) => throw new InvalidOperationException("No file operation should run."),
            () => throw new InvalidOperationException("No backup should run."), () => activity, async () =>
            {
                pauseCalled = true;
                activity = activity with { Summary = "Останавливаю", Paused = true, CanToggle = false };
                await drain.Task;
                activity = activity with { Summary = "Приостановлено", CanToggle = true };
            });
        try
        {
            window.Show();
            await Task.Delay(600);
            var buttons = Descendants<Button>(window).ToArray();
            var pause = Assert.Single(buttons, b => b.Content?.ToString() == "Пауза индексирования и автообновления");
            Assert.Contains(Descendants<TextBlock>(window), text => text.Text.Contains("fixture.png"));
            Assert.All(Descendants<DataGrid>(window), grid => Assert.Empty(grid.Items));
            pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(pauseCalled);
            Assert.False(pause.IsEnabled);
            await Task.Delay(600);
            Assert.False(pause.IsEnabled);
            Assert.Contains(Descendants<TextBlock>(window), text => text.Text == "Останавливаю");
            drain.SetResult();
            await Task.Delay(600);
            Assert.True(pause.IsEnabled);
            Assert.Equal("Продолжить фоновую обработку", pause.Content);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally { drain.TrySetResult(); await Task.Yield(); window.Close(); Directory.Delete(directory, true); }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
