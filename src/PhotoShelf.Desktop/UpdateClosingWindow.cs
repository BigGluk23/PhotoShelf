using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace PhotoShelf.Desktop;

/// <summary>Independent, responsive cancellation surface while the library drains its actual readers.</summary>
internal sealed class UpdateClosingWindow : Window
{
    private readonly TextBlock _stage;
    private readonly Action _defer;
    private readonly System.Windows.Controls.Button _later;
    private bool _committed;
    private bool _finished;
    public UpdateClosingWindow(Action defer)
    {
        _defer = defer;
        Title = "Подготовка обновления PhotoShelf"; Width = 510; Height = 205;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(22) };
        _stage = new TextBlock { Text = "Завершаю фоновые операции и сохраняю состояние…", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_stage);
        panel.Children.Add(new System.Windows.Controls.ProgressBar { Height = 5, IsIndeterminate = true, Margin = new Thickness(0, 18, 0, 18) });
        _later = new System.Windows.Controls.Button { Content = "Отложить обновление", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        _later.Click += (_, _) => { if (!_committed) { _later.IsEnabled = false; _defer(); } };
        panel.Children.Add(_later); Content = panel;
    }
    public void SetStage(string text) => _stage.Text = text;
    public void Finish() { _finished = true; Close(); }
    public void CommitLaunch() { _committed = true; _later.IsEnabled = false; }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished) { e.Cancel = true; if (!_committed) _defer(); }
        base.OnClosing(e);
    }
}
