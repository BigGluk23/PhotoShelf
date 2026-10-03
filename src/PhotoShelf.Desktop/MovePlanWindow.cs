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
    private readonly bool _quarantineMode;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, EnableRowVirtualization = true };
    private readonly ComboBox _collision = new() { ItemsSource = new[] { "Совпадения: пропустить", "Совпадения: новое имя" }, SelectedIndex = 0, Margin = new Thickness(8) };
    private readonly Button _execute = new() { Content = "Выполнить перенос", Margin = new Thickness(8), Padding = new Thickness(12, 6, 12, 6) };
    private int _revision;
    private CancellationTokenSource? _planning;
    private readonly ComboBox _layout = new() { ItemsSource = new[] { "В выбранную папку", "Год", "Год / месяц", "Год / месяц / день" }, Margin = new Thickness(8), Width = 200 };
    private readonly ComboBox _names = new() { ItemsSource = new[] { "Сохранить имена", "Дата съёмки + имя" }, SelectedIndex = 0, Margin = new Thickness(8), Width = 190 };
    private readonly System.Windows.Controls.TextBox _event = new() { Margin = new Thickness(8), Width = 240, ToolTip = "Необязательная подпапка события. Например: Отпуск" };
    private readonly TextBlock _summary = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    public IReadOnlyList<MoveEntry> Plan { get; private set; } = Array.Empty<MoveEntry>();
    public MovePlanWindow(MoveRequest[] requests, string destination, bool byYear, bool quarantineMode = false)
    {
        _requests = requests; _destination = destination; _byYear = byYear; _quarantineMode = quarantineMode;
        Title = quarantineMode ? "Предварительный план карантина" : "Предварительный план переноса";
        Width = 1050; Height = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel(); Content = panel;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(new TextBlock { Text = quarantineMode
            ? $"Карантин: {destination}\nОригиналы не удаляются: отмеченные точные дубли и их подтверждённые связанные файлы будут перенесены в отдельную папку. Перед переносом PhotoShelf повторно проверит сохраняемые копии."
            : $"Папка: {destination}\nВнутри тома — переименование, между томами — проверенная копия. Совпадения не перезаписываются. Связанные файлы показаны отдельными строками.", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap });
        if (StoragePrivacyPolicy.KnownSynchronizationWarning(destination) is { } synchronizationWarning)
            top.Children.Add(new TextBlock { Text = synchronizationWarning, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.DarkOrange });
        top.Children.Add(_collision);
        var options = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        _layout.SelectedIndex = byYear ? 2 : 0;
        options.Children.Add(_layout); options.Children.Add(_names);
        options.Children.Add(new TextBlock { Text = "Событие:", VerticalAlignment = VerticalAlignment.Center }); options.Children.Add(_event);
        if (quarantineMode) options.Visibility = Visibility.Collapsed;
        top.Children.Add(options); top.Children.Add(_summary);
        if (quarantineMode) _execute.Content = "Подтвердить перенос в карантин";
        DockPanel.SetDock(_execute, Dock.Bottom); panel.Children.Add(_execute);
        _grid.Columns.Add(new DataGridTextColumn { Header = "Откуда", Binding = new System.Windows.Data.Binding(nameof(MoveEntry.Source)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Куда", Binding = new System.Windows.Data.Binding(nameof(MoveEntry.Destination)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Пропуск", Binding = new System.Windows.Data.Binding(nameof(MoveEntry.SkipReason)), Width = 160 });
        panel.Children.Add(_grid);
        _collision.SelectionChanged += async (_, _) => await RefreshAsync();
        _layout.SelectionChanged += async (_, _) => await RefreshAsync();
        _names.SelectionChanged += async (_, _) => await RefreshAsync();
        _event.TextChanged += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { ++_revision; _planning?.Cancel(); };
        _execute.Click += (_, _) => { DialogResult = true; };
    }
    private async Task RefreshAsync()
    {
        var revision = ++_revision;
        _planning?.Cancel();
        using var operation = new CancellationTokenSource(); _planning = operation;
        _execute.IsEnabled = false;
        var collision = _collision.SelectedIndex == 0 ? CollisionPolicy.Skip : CollisionPolicy.Rename;
        try
        {
            var layout = _quarantineMode ? new MoveLayoutOptions() :
                new MoveLayoutOptions((FolderLayout)Math.Max(0, _layout.SelectedIndex), (FileNameStyle)Math.Max(0, _names.SelectedIndex), _event.Text.Trim());
            await Task.Delay(120, operation.Token);
            var plan = await Task.Run(() => new FileMoveService().Plan(_requests, _destination, collision, _byYear, operation.Token, layout), operation.Token);
            if (revision != _revision) return;
            Plan = plan; _grid.ItemsSource = plan;
            _summary.Text = $"К переносу: {plan.Count(x => x.SkipReason is null):N0} · Пропусков: {plan.Count(x => x.SkipReason is not null):N0} · {plan.Where(x => x.SkipReason is null).Sum(x => x.Length) / 1048576d:N1} МиБ";
            _execute.IsEnabled = plan.Any(x => x.SkipReason is null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (revision == _revision) _summary.Text = $"План недоступен: {ex.Message}"; }
        finally { if (ReferenceEquals(_planning, operation)) _planning = null; }
    }
}
