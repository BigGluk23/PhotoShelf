using System.Windows;
using Forms = System.Windows.Forms;

namespace PhotoShelf.Desktop;

public partial class DuplicateSearchDialog : Window
{
    public DuplicateSearchDialog(
        int currentViewCount,
        int includedFoldersCount,
        int wholeLibraryCount,
        string? currentFolder)
    {
        InitializeComponent();

        CurrentViewDescription.Text = $"То, что сейчас видно в сетке с учётом поиска, фильтров и режима просмотра. Файлов: {currentViewCount}.";
        IncludedFoldersDescription.Text = $"Все файлы из папок, которые включены галками слева. Файлов: {includedFoldersCount}.";
        WholeLibraryDescription.Text = $"Все найденные файлы в каталоге PhotoShelf, кроме скрытых типом видео/фото. Файлов: {wholeLibraryCount}.";

        if (string.IsNullOrWhiteSpace(currentFolder))
        {
            CurrentFolderRadio.IsEnabled = false;
            CurrentFolderDescription.Text = "Доступно, когда слева выбрана конкретная папка.";
        }
        else
        {
            CurrentFolderDescription.Text = $"Только эта папка и её подпапки: {currentFolder}";
        }
    }

    public DuplicateSearchScope SelectedScope { get; private set; } = DuplicateSearchScope.CurrentView;

    public string? CompareFolderA => string.IsNullOrWhiteSpace(FolderABox.Text) ? null : FolderABox.Text;

    public string? CompareFolderB => string.IsNullOrWhiteSpace(FolderBBox.Text) ? null : FolderBBox.Text;

    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        if (CompareFoldersRadio.IsChecked == true &&
            (string.IsNullOrWhiteSpace(CompareFolderA) || string.IsNullOrWhiteSpace(CompareFolderB)))
        {
            System.Windows.MessageBox.Show(
                "Выберите две папки для сравнения.",
                "Дубликаты",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

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

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnBrowseFolderAClicked(object sender, RoutedEventArgs e)
    {
        BrowseInto(FolderABox);
    }

    private void OnBrowseFolderBClicked(object sender, RoutedEventArgs e)
    {
        BrowseInto(FolderBBox);
    }

    private void BrowseInto(System.Windows.Controls.TextBox target)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Выберите папку для сравнения дублей",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            target.Text = dialog.SelectedPath;
            CompareFoldersRadio.IsChecked = true;
        }
    }
}
