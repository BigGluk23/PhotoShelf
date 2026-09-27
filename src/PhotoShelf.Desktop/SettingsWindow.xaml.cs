using System.Diagnostics;
using System.IO;
using System.Windows;

namespace PhotoShelf.Desktop;

public partial class SettingsWindow : Window
{
    public SettingsWindow(bool showVideos, bool includeSystemFolders)
    {
        InitializeComponent();
        ShowVideosCheckBox.IsChecked = showVideos;
        IncludeSystemFoldersCheckBox.IsChecked = includeSystemFolders;
        CatalogPathBox.Text = LocalCatalogStore.CatalogDirectory;
    }

    public bool ShowVideos { get; private set; }

    public bool IncludeSystemFolders { get; private set; }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        ShowVideos = ShowVideosCheckBox.IsChecked == true;
        IncludeSystemFolders = IncludeSystemFoldersCheckBox.IsChecked == true;
        DialogResult = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnOpenCatalogClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LocalCatalogStore.CatalogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{LocalCatalogStore.CatalogDirectory}\"",
            UseShellExecute = true
        });
    }

    private void OnClearThumbnailCacheClicked(object sender, RoutedEventArgs e)
    {
        var cachePath = Path.Combine(LocalCatalogStore.CatalogDirectory, "thumb-cache");
        if (!Directory.Exists(cachePath))
        {
            System.Windows.MessageBox.Show("Кэш миниатюр уже пуст.", "PhotoShelf", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = System.Windows.MessageBox.Show(
            "Очистить кэш миниатюр?\n\nОригинальные фото и видео не будут затронуты.",
            "PhotoShelf",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            Directory.Delete(cachePath, recursive: true);
            System.Windows.MessageBox.Show("Кэш миниатюр очищен.", "PhotoShelf", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось очистить кэш.\n\n{ex.Message}", "PhotoShelf", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
