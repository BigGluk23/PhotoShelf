using System.Windows;
using System.Windows.Controls;
using PhotoShelf.Application.Files;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;

namespace PhotoShelf.Desktop;

public sealed class MovePlanWindow : Window
{
    private readonly MoveRequest[] _requests;
    private readonly string _destination;
    private readonly bool _byYear;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, EnableRowVirtualization = true };
    private readonly ComboBox _collision = new() { ItemsSource = new[] { "Совпадения: пропустить", "Совпадения: новое имя" }, SelectedIndex = 0, Margin = new Thickness(8) };
    private readonly Button _execute = new() { Content = "Выполнить перенос", Margin = new Thickness(8), Padding = new Thickness(12, 6, 12, 6) };
    private int _revision;
    public IReadOnlyList<MoveEntry> Plan { get; private set; } = Array.Empty<MoveEntry>();
    public MovePlanWindow(MoveRequest[] requests, string destination, bool byYear)
    {
        _requests = requests; _destination = destination; _byYear = byYear;
        Title = "Предварительный план переноса"; Width = 1050; Height = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel(); Content = panel;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(new TextBlock { Text = $"Папка: {destination}\nОригиналы перемещаются после проверки копии. Совпадения никогда не перезаписываются.", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap });
        top.Children.Add(_collision);
        DockPanel.SetDock(_execute, Dock.Bottom); panel.Children.Add(_execute);
        _grid.Columns.Add(new DataGridTextColumn { Header = "Откуда", Binding = new System.Windows.Data.Binding(nameof(MoveEntry.Source)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Куда", Binding = new System.Windows.Data.Binding(nameof(MoveEntry.Destination)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Пропуск", Binding = new System.Windows.Data.Binding(nameof(MoveEntry.SkipReason)), Width = 160 });
        panel.Children.Add(_grid);
        _collision.SelectionChanged += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => await RefreshAsync();
        _execute.Click += (_, _) => { DialogResult = true; };
    }
    private async Task RefreshAsync()
    {
        var revision = ++_revision;
        _execute.IsEnabled = false;
        var collision = _collision.SelectedIndex == 0 ? CollisionPolicy.Skip : CollisionPolicy.Rename;
        try
        {
            var plan = await Task.Run(() => new FileMoveService().Plan(_requests, _destination, collision, _byYear));
            if (revision != _revision) return;
            Plan = plan; _grid.ItemsSource = plan;
            _execute.IsEnabled = plan.Any(x => x.SkipReason is null);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Не удалось подготовить перенос"); }
    }
}
