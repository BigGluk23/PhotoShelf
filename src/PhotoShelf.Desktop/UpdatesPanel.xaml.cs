using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace PhotoShelf.Desktop;

public partial class UpdatesPanel : System.Windows.Controls.UserControl
{
    public UpdatesPanel() => InitializeComponent();
    private UpdateCenter? Center => DataContext as UpdateCenter;
    private async void OnAutoCheckClicked(object sender, RoutedEventArgs e)
    { if (Center is { } center) await center.SetAutoCheckAsync(AutoUpdateCheckBox.IsChecked == true); }
    private async void OnCheckClicked(object sender, RoutedEventArgs e)
    { if (Center is { } center) await center.CheckAsync(); }
    private async void OnDownloadClicked(object sender, RoutedEventArgs e)
    { if (Center is { } center) await center.DownloadAsync(); }
    private void OnCancelDownloadClicked(object sender, RoutedEventArgs e) => Center?.CancelDownload();
    private async void OnInstallClicked(object sender, RoutedEventArgs e)
    { if (Center is { } center) await center.InstallAsync(); }
    private async void OnLaterClicked(object sender, RoutedEventArgs e)
    { if (Center is { } center) await center.DismissAsync(false); }
    private async void OnSkipClicked(object sender, RoutedEventArgs e)
    { if (Center is { } center) await center.DismissAsync(true); }
    private void OnNotesClicked(object sender, RoutedEventArgs e)
    {
        if (Center?.ReleaseNotesUrl is not { } url) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { /* An unavailable browser must not interrupt the library. */ }
    }
}
