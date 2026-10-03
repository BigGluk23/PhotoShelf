using System.Windows;
using Forms = System.Windows.Forms;

namespace PhotoShelf.Desktop;

public partial class DuplicateSearchDialog : Window
{
    public DuplicateSearchDialog(int currentViewCount, int includedFoldersCount,
        int wholeLibraryCount, string? currentFolder)
    {
        InitializeComponent();

        CurrentViewDescription.Text = $"То, что сейчас видно в сетке с учётом поиска, фильтров и режима просмотра. Файлов: {currentViewCount:N0}.";
        IncludedFoldersDescription.Text = $"Все файлы из папок, которые включены галочками слева. Файлов: {includedFoldersCount:N0}.";
        WholeLibraryDescription.Text = $"Все фотографии в каталоге PhotoShelf. Файлов: {wholeLibraryCount:N0}.";

        if (string.IsNullOrWhiteSpace(currentFolder))
        {
            CurrentFolderRadio.IsEnabled = false;
            CurrentFolderDescription.Text = "Доступно, когда слева выбрана конкретная папка.";
        }
        else
        {
            CurrentFolderDescription.Text = $"Только эта папка и её подпапки: {currentFolder}";
        }
        UpdateModeText();
    }

    public DuplicateSearchMode SelectedMode { get; private set; } = DuplicateSearchMode.Exact;
    public DuplicateSearchScope SelectedScope { get; private set; } = DuplicateSearchScope.CurrentView;
    public string? CompareFolderA => string.IsNullOrWhiteSpace(FolderABox.Text) ? null : FolderABox.Text;
    public string? CompareFolderB => string.IsNullOrWhiteSpace(FolderBBox.Text) ? null : FolderBBox.Text;

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateModeText();
    }

    private void UpdateModeText()
    {
        var similar = SimilarModeRadio.IsChecked == true;
        ScopeTitleText.Text = similar ? "Где искать похожие фотографии?" : "Где искать точные дубликаты?";
        SafetyText.Text = similar
            ? "Сходство — подсказка, а не доказательство дубля. На первом этапе здесь нет массового удаления."
            : "Перемещение в карантин будет только после отдельного подтверждения.";
    }

    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        if (CompareFoldersRadio.IsChecked == true &&
            (string.IsNullOrWhiteSpace(CompareFolderA) || string.IsNullOrWhiteSpace(CompareFolderB)))
        {
            System.Windows.MessageBox.Show("Выберите две папки для сравнения.", "Поиск повторов",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedMode = SimilarModeRadio.IsChecked == true ? DuplicateSearchMode.Similar : DuplicateSearchMode.Exact;
        SelectedScope = WholeLibraryRadio.IsChecked == true
            ? DuplicateSearchScope.WholeLibrary
            : IncludedFoldersRadio.IsChecked == true
                ? DuplicateSearchScope.IncludedFolders
                : CurrentFolderRadio.IsChecked == true
                    ? DuplicateSearchScope.CurrentFolder
                    : CompareFoldersRadio.IsChecked == true
                        ? DuplicateSearchScope.CompareTwoFolders
                        : DuplicateSearchScope.CurrentView;
        DialogResult = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnBrowseFolderAClicked(object sender, RoutedEventArgs e) => BrowseInto(FolderABox);
    private void OnBrowseFolderBClicked(object sender, RoutedEventArgs e) => BrowseInto(FolderBBox);

    private void BrowseInto(System.Windows.Controls.TextBox target)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Выберите папку для сравнения",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        target.Text = dialog.SelectedPath;
        CompareFoldersRadio.IsChecked = true;
    }
}
