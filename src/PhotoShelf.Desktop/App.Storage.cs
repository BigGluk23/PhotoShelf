using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PhotoShelf.Application.Media;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Infrastructure.Sqlite;
using Button = System.Windows.Controls.Button;

namespace PhotoShelf.Desktop;

public partial class App
{
    private static FileStream AcquireLease(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        return new(Path.Combine(directory, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private async Task<LocalCatalogState?> PrepareCatalogStorageAsync(LoadingWindow loading)
    {
        if (LocalCatalogStore.IsIsolatedSmokeCatalog)
        {
            _catalogLease = await Task.Run(() => AcquireLease(LocalCatalogStore.CatalogDirectory, "writer.lock"));
            return null;
        }
        loading.SetStage("Проверяю совместимость и расположение каталога…");
        var location = LocalCatalogStore.StorageLocation;
        var discovery = await Task.Run(() =>
        {
            // v0.9.7 predates the named mutex. Do not terminate another copy or race its file moves.
            RejectOlderRunningCopy();
            _startupLease = AcquireLease(location.LocalDirectory, "startup-v1.lock");
            return location.Discover();
        });
        if (discovery.Selection is { } existing)
        {
            return await Task.Run(async () =>
            {
                _catalogLease = AcquireLease(existing.DirectoryPath, "writer.lock");
                var store = new SqliteDesktopCatalogStore(existing.DirectoryPath);
                await store.InitializeAndImportLegacyAsync(Path.Combine(existing.DirectoryPath, "catalog-v1.json"), requirePublishedGeneration: true);
                var state = await store.LoadAsync(includeItems: false);
                location.PinExisting(existing);
                return state;
            });
        }
        var unavailable = discovery.Candidates.Where(candidate => !candidate.IsAvailable).ToArray();
        if (unavailable.Length > 0)
            throw new IOException("Не удалось проверить прежние каталоги. Новая пустая библиотека не создана.\n" +
                string.Join("\n", unavailable.Select(candidate => candidate.DirectoryPath + ": " + candidate.Error)));
        var candidates = discovery.Candidates.Where(candidate => candidate.HasEvidence).ToArray();
        var source = candidates.Length switch
        {
            0 => null,
            1 => candidates[0],
            _ => ChooseCatalog(loading, candidates)
        };
        loading.SetStage("Создаю проверенную рабочую копию. Прежний каталог сохраняется…");
        using var prepared = await Task.Run(async () =>
        {
            if (source?.HasDatabase == true)
                await new SqliteDesktopCatalogStore(source.DirectoryPath).ValidateCompatibilityAsync();
            return await new CatalogGenerationMigration().PrepareAsync(location, source, CancellationToken.None);
        });
        loading.SetStage("Проверяю базу и переношу прежние настройки…");
        var preparedState = await Task.Run(async () =>
        {
            _catalogLease = AcquireLease(prepared.DirectoryPath, "writer.lock");
            var store = new SqliteDesktopCatalogStore(prepared.DirectoryPath);
            await store.InitializeAndImportLegacyAsync(Path.Combine(prepared.DirectoryPath, "catalog-v1.json"));
            var state = await store.LoadAsync(includeItems: false);
            // A failed initialization never publishes a new active catalog.
            location.CommitPrepared(prepared);
            return state;
        });
        loading.SetStage("Каталог проверен. Восстанавливаю выбранный вид…");
        return preparedState;
    }

    private static CatalogStorageCandidate ChooseCatalog(Window owner, IReadOnlyList<CatalogStorageCandidate> candidates)
    {
        CatalogStorageCandidate? selected = null;
        var window = new Window { Title = "Найдены две библиотеки PhotoShelf", Owner = owner, Width = 660,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Выберите прежнюю библиотеку для обновления. Оба исходных каталога будут сохранены. Автоматическое объединение не выполняется.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var candidate in candidates)
        {
            var button = new Button { Content = new TextBlock { Text = candidate.DirectoryPath, TextWrapping = TextWrapping.Wrap },
                Padding = new Thickness(12), Margin = new Thickness(0, 4, 0, 4), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left };
            button.Click += (_, _) => { selected = candidate; window.DialogResult = true; };
            panel.Children.Add(button);
        }
        window.Content = panel;
        if (window.ShowDialog() != true || selected is null)
            throw new OperationCanceledException("Выбор библиотеки отменён. Каталоги сохранены без изменений.");
        return selected;
    }

    private static void RejectOlderRunningCopy()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == current.Id) continue;
                try
                {
                    if (process.ProcessName.Equals("PhotoShelf", StringComparison.OrdinalIgnoreCase) ||
                        process.ProcessName.Equals(current.ProcessName, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Обнаружена другая копия PhotoShelf. Закройте её перед обновлением; её работа не была прервана.");
                }
                catch (InvalidOperationException) { /* Process exited while enumerating. */ }
            }
        }
    }

    private static async Task CheckHeifInstallationAsync(Window owner)
    {
        try { await Task.Run(() => new HeifDecoderClient().VerifyInstallationAsync(CancellationToken.None)); }
        catch (HeifInstallationException error)
        {
            System.Windows.MessageBox.Show(owner, error.Message + "\n\nКаталог открыт. HEIC/HEIF будут доступны после восстановления комплекта программы.",
                "Компоненты HEIC/HEIF требуют восстановления", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
