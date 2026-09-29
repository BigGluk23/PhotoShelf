using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace PhotoShelf.Desktop;

internal static class QuarantineConfiguration
{
    private static string[] _knownBatches = Array.Empty<string>();
    private static string? LegacyRoot => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) is { Length: > 0 } pictures
        ? Path.Combine(pictures, "PhotoShelf_Quarantine") : null;

    public static void ApplyLoaded(LocalCatalogState state) =>
        Volatile.Write(ref _knownBatches, state.QuarantineBatchDirectories.ToArray());

    public static bool IsKnownQuarantine(string path) =>
        Volatile.Read(ref _knownBatches).Any(root => StoragePrivacyPolicy.IsUnder(path, root)) ||
        (LegacyRoot is { } legacy && StoragePrivacyPolicy.IsUnder(path, legacy));

    public static async Task<bool> IsQuarantineDestinationAsync(string path)
    {
        ApplyLoaded(await new SqliteDesktopCatalogStore().LoadAsync(includeItems: false));
        return IsKnownQuarantine(path);
    }

    public static async Task RegisterBatchAsync(string path)
    {
        var store = new SqliteDesktopCatalogStore();
        await store.RegisterQuarantineBatchAsync(path);
        ApplyLoaded(await store.LoadAsync(includeItems: false));
    }

    public static async Task<string?> GetOrChooseRootAsync(Window owner, bool forceSelection = false)
    {
        var state = await new SqliteDesktopCatalogStore().LoadAsync(includeItems: false);
        ApplyLoaded(state);
        if (!forceSelection && state.QuarantineDirectory is { Length: > 0 } current)
            return current;
        using var picker = new Forms.FolderBrowserDialog
        {
            Description = "Выберите папку для карантина. Здесь будут храниться полные оригиналы дублей.",
            UseDescriptionForTitle = true, ShowNewFolderButton = true,
            SelectedPath = state.QuarantineDirectory ?? ""
        };
        if (picker.ShowDialog() != Forms.DialogResult.OK) return null;
        var root = Path.GetFullPath(picker.SelectedPath);
        if (CatalogStoragePaths.InternalRoots.Any(storage => StoragePrivacyPolicy.IsUnder(root, storage)))
        {
            System.Windows.MessageBox.Show(owner, "Карантин должен находиться вне служебной папки PhotoShelf и кэша.", "Выберите другую папку");
            return null;
        }
        var warning = StoragePrivacyPolicy.KnownSynchronizationWarning(root);
        var text = $"Карантин:\n{root}\n\nЗдесь будут храниться полные оригиналы. Для каждого переноса PhotoShelf создаст отдельную папку и покажет план.\n\n" +
            (warning ?? "PhotoShelf не может определить все программы синхронизации и резервного копирования. Проверьте настройки выбранной папки.");
        if (System.Windows.MessageBox.Show(owner, text, "Сохранить папку карантина?", MessageBoxButton.OKCancel,
            warning is null ? MessageBoxImage.Information : MessageBoxImage.Warning) != MessageBoxResult.OK) return null;
        await new SqliteDesktopCatalogStore().SetQuarantineDirectoryAsync(root);
        return root;
    }
}
