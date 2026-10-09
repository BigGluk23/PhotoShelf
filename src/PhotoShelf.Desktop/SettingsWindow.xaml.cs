using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using PhotoShelf.Application.Diagnostics;
using PhotoShelf.Application.Background;

namespace PhotoShelf.Desktop;

public partial class SettingsWindow : Window
{
    public SettingsWindow(bool showVideos, bool includeSystemFolders, UpdateCenter? updates = null, BackgroundLoadMode loadMode = BackgroundLoadMode.Balanced)
    {
        InitializeComponent();
        UpdatesSection.DataContext = updates;
        UpdatesSection.Visibility = updates is null ? Visibility.Collapsed : Visibility.Visible;
        BackgroundLoadModeBox.SelectedIndex = Enum.IsDefined(loadMode) ? (int)loadMode : 1;
        ShowVideosCheckBox.IsChecked = showVideos;
        IncludeSystemFoldersCheckBox.IsChecked = includeSystemFolders;
        CatalogPathBox.Text = LocalCatalogStore.CatalogDirectory;
        DerivedPathBox.Text = LocalCatalogStore.DerivedDataDirectory;
        var warnings = new List<string>();
        if (LocalCatalogStore.UsesLegacyStorage)
            warnings.Add("Сохранён прежний каталог в roaming-профиле: база, журналы и резервные копии остаются вместе. Автоматический перенос не выполняется. Старые файлы диагностики и кэша могут оставаться здесь; папка может синхронизироваться политикой профиля.");
        foreach (var path in new[] { LocalCatalogStore.CatalogDirectory, LocalCatalogStore.DerivedDataDirectory }.Distinct())
            if (StoragePrivacyPolicy.KnownSynchronizationWarning(path) is { } warning) warnings.Add(warning);
        StorageWarningText.Text = string.Join("\n", warnings);
    }

    public BackgroundLoadMode LoadMode { get; private set; } = BackgroundLoadMode.Balanced;
    public bool ShowVideos { get; private set; }
    public bool IncludeSystemFolders { get; private set; }

    private async void OnSettingsLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (UpdatesSection.DataContext is UpdateCenter updates) await updates.InitializeAsync();
            var state = await new SqliteDesktopCatalogStore().LoadAsync(includeItems: false);
            if (IsLoaded) QuarantinePathBox.Text = state.QuarantineDirectory ?? "Папка ещё не выбрана";
        }
        catch (Exception ex) { if (IsLoaded) SettingsStatusText.Text = $"Не удалось прочитать настройки карантина: {ex.Message}"; }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        LoadMode = (BackgroundLoadMode)Math.Clamp(BackgroundLoadModeBox.SelectedIndex, 0, 2);
        ShowVideos = ShowVideosCheckBox.IsChecked == true;
        IncludeSystemFolders = IncludeSystemFoldersCheckBox.IsChecked == true;
        DialogResult = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void OnOpenCatalogClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Run(() => Directory.CreateDirectory(LocalCatalogStore.CatalogDirectory));
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{LocalCatalogStore.CatalogDirectory}\"", UseShellExecute = true });
        }
        catch (Exception ex) { if (IsLoaded) SettingsStatusText.Text = ex.Message; }
    }

    private async void OnClearThumbnailCacheClicked(object sender, RoutedEventArgs e)
    {
        if (!ClearCacheButton.IsEnabled) return;
        var result = System.Windows.MessageBox.Show(this,
            $"Очистить созданные PhotoShelf миниатюры?\n\nТекущий кэш:\n{ThumbnailCache.DirectoryPath}\n\nОригиналы, база, журналы и посторонние файлы не затрагиваются. Старые запросы превью не смогут заново записать очищенный кэш.",
            "Очистка миниатюр", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;
        ClearCacheButton.IsEnabled = false;
        SettingsStatusText.Text = "Очищаю миниатюры в фоне…";
        try
        {
            var cleared = await ThumbnailCache.ClearAsync();
            if (IsLoaded) SettingsStatusText.Text = $"Удалено миниатюр: {cleared.RemovedCount:N0}. Осталось созданных PhotoShelf файлов: {cleared.BytesAfter:N0} байт. Посторонние файлы сохранены.";
        }
        catch (Exception ex) { if (IsLoaded) SettingsStatusText.Text = $"Очистка остановлена: {ex.Message}"; }
        finally { if (IsLoaded) ClearCacheButton.IsEnabled = true; }
    }

    private async void OnChooseQuarantineClicked(object sender, RoutedEventArgs e)
    {
        ChooseQuarantineButton.IsEnabled = false;
        try
        {
            if (await QuarantineConfiguration.GetOrChooseRootAsync(this, forceSelection: true) is { } root && IsLoaded)
            {
                QuarantinePathBox.Text = root;
                SettingsStatusText.Text = "Папка карантина сохранена. Прежние файлы и история остаются на месте.";
            }
        }
        catch (Exception ex) { if (IsLoaded) SettingsStatusText.Text = $"Не удалось сохранить карантин: {ex.Message}"; }
        finally { if (IsLoaded) ChooseQuarantineButton.IsEnabled = true; }
    }

    private async void OnExportDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить обезличенную сводку", Filter = "JSON (*.json)|*.json", DefaultExt = ".json",
            FileName = $"PhotoShelf-support-{DateTime.Now:yyyyMMdd-HHmmss}.json", AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var summary = SupportSnapshot.Create(ErrorReporter.Version, LocalCatalogStore.UsesLegacyStorage, ErrorReporter.ErrorCount,
                BackgroundWorkController.Shared, BackgroundWorkScheduler.Shared.PendingCount, BackgroundWorkScheduler.Shared.RunningCount);
            await Task.Run(() =>
            {
                using var file = new FileStream(dialog.FileName, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                var bytes = Encoding.UTF8.GetBytes(summary); file.Write(bytes); file.Flush(true);
            });
            if (IsLoaded) SettingsStatusText.Text = "Сводка сохранена. Фото, пути, содержимое базы и исходные журналы не включены.";
        }
        catch (Exception ex) { if (IsLoaded) SettingsStatusText.Text = $"Экспорт не выполнен (существующие файлы не перезаписываются): {ex.Message}"; }
    }
}
