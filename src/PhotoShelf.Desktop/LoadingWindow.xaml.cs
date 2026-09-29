using System.Windows;

namespace PhotoShelf.Desktop;

public partial class LoadingWindow : Window
{
    public void SetStage(string text) => StageText.Text = text;

    public LoadingWindow()
    {
        InitializeComponent();
    }
}
