using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PhotoShelf.Application.Files;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Orientation = System.Windows.Controls.Orientation;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;

namespace PhotoShelf.Desktop;

public sealed class OperationsWindow : Window
{
    private readonly string _directory;
    private readonly Func<MoveOperationHistory, bool, Task> _execute;
    private readonly DataGrid _history = new() { IsReadOnly = true, AutoGenerateColumns = false, EnableRowVirtualization = true };
    private readonly DataGrid _files = new() { IsReadOnly = true, AutoGenerateColumns = false, EnableRowVirtualization = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly Button _recover = new() { Content = "Продолжить / восстановить", Margin = new Thickness(6), Padding = new Thickness(8) };
    private readonly Button _undo = new() { Content = "Откатить перенос", Margin = new Thickness(6), Padding = new Thickness(8) };
    private readonly Button _export = new() { Content = "Сохранить журнал для разбора", Margin = new Thickness(6), Padding = new Thickness(8) };
    private bool _busy;
    private int _revision;
    public OperationsWindow(string directory, Func<MoveOperationHistory, bool, Task> execute, Func<Task<string>> backup)
    {
        _directory = directory; _execute = execute;
        Title = "Операции и восстановление"; Width = 1100; Height = 720; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(10) }; Content = grid;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _status.Text = "Выберите операцию. Внизу показаны исходные и целевые пути. При откате направление меняется. Совпадения и изменённые файлы не перезаписываются.";
        grid.Children.Add(_status);
        Grid.SetRow(_history, 1); grid.Children.Add(_history);
        Grid.SetRow(_files, 2); grid.Children.Add(_files);
        AddColumn(_history, "Начало (UTC)", nameof(MoveOperationHistory.StartedUtc), 160);
        AddColumn(_history, "Файлов", nameof(MoveOperationHistory.TotalFiles), 65);
        AddColumn(_history, "Готово", nameof(MoveOperationHistory.CompletedFiles), 65);
        AddColumn(_history, "Ожидают", nameof(MoveOperationHistory.PendingFiles), 75);
        AddColumn(_history, "Откачено", nameof(MoveOperationHistory.IsUndone), 75);
        AddColumn(_history, "Проблема", nameof(MoveOperationHistory.Error), 280);
        AddColumn(_history, "Журнал", nameof(MoveOperationHistory.JournalPath), 260);
        AddColumn(_files, "Исходный путь", nameof(MoveEntry.Source), 420);
        AddColumn(_files, "Целевой путь", nameof(MoveEntry.Destination), 420);
        AddColumn(_files, "Размер", nameof(MoveEntry.Length), 90);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_recover); buttons.Children.Add(_undo);
        buttons.Children.Add(_export);
        _export.Click += async (_, _) => await ExportJournalAsync();
        var refresh = new Button { Content = "Обновить", Padding = new Thickness(8), Margin = new Thickness(6) };
        var backupButton = new Button { Content = "Резервная копия каталога", Padding = new Thickness(8), Margin = new Thickness(6) };
        backupButton.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true; SetButtons(); backupButton.IsEnabled = false;
            try { _status.Text = $"Проверенная копия каталога: {await backup()}\nЭто копия индекса и настроек; резервное копирование оригиналов выполняется отдельно."; }
            catch (Exception ex) { _status.Text = $"Копия не создана: {ex.Message}"; }
            finally { _busy = false; backupButton.IsEnabled = true; SetButtons(); }
        };
        buttons.Children.Add(backupButton);
        buttons.Children.Add(refresh); Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        refresh.Click += async (_, _) => { if (!_busy) await RefreshAsync(); };
        _recover.Click += async (_, _) => await ExecuteAsync(false);
        _undo.Click += async (_, _) => await ExecuteAsync(true);
        _history.SelectionChanged += async (_, _) => await ShowPlanAsync();
        Loaded += async (_, _) => await RefreshAsync();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
    }
    private static void AddColumn(DataGrid grid, string title, string property, double width) =>
        grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = width });
    private async Task RefreshAsync()
    {
        try
        {
            _history.ItemsSource = await Task.Run(() => new FileMoveService().ReadHistory(_directory));
            if (_history.Items.Count > 0) _history.SelectedIndex = 0;
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        SetButtons();
    }
    private async Task ShowPlanAsync()
    {
        var revision = ++_revision; SetButtons();
        if (_history.SelectedItem is not MoveOperationHistory operation) return;
        try
        {
            var plan = await Task.Run(() => new FileMoveService().ReadPlan(operation.JournalPath));
            if (revision == _revision) _files.ItemsSource = plan;
        }
        catch (Exception ex) { _files.ItemsSource = null; _status.Text = $"Автоматическое восстановление недоступно: {ex.Message}"; }
    }
    private void SetButtons()
    {
        var operation = _history.SelectedItem as MoveOperationHistory;
        _recover.IsEnabled = !_busy && operation is { PendingFiles: > 0 };
        _undo.IsEnabled = !_busy && operation?.CanUndo == true;
        _export.IsEnabled = !_busy && operation is not null;
        _history.IsEnabled = !_busy;
    }
    private async Task ExportJournalAsync()
    {
        if (_busy || _history.SelectedItem is not MoveOperationHistory operation) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Новое имя для копии журнала (содержит полные пути; перезапись запрещена)",
            FileName = "PhotoShelf-operation-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl",
            Filter = "Журнал PhotoShelf|*.jsonl", OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true) return;
        _busy = true; SetButtons();
        try
        {
            var hash = await JournalEvidenceExport.CopyAsync(_directory, operation.JournalPath, dialog.FileName);
            _status.Text = $"Копия для ручного разбора: {dialog.FileName}\nSHA-256: {hash}\nОригинал журнала и медиа не изменены. Копия содержит полные пути; отправляйте её только осознанно. Не удаляйте журналы и .photoshelf-* файлы для снятия блокировки.";
        }
        catch (Exception ex) { _status.Text = $"Экспорт не завершён: {ex.Message}\nОригинал сохранён; незавершённая копия имеет суффикс .partial."; }
        finally { _busy = false; SetButtons(); }
    }
    private async Task ExecuteAsync(bool undo)
    {
        if (_busy || _history.SelectedItem is not MoveOperationHistory operation) return;
        var text = undo ? "Вернуть файлы по исходным путям? Откат будет записан отдельной операцией. Конфликты и изменение содержимого остановят перенос." : "Продолжить операцию по указанным путям? Каждый незавершённый шаг будет повторно проверен.";
        if (System.Windows.MessageBox.Show(this, text, undo ? "Подтвердить откат" : "Подтвердить восстановление", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _busy = true; SetButtons();
        try { await _execute(operation, undo); }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _busy = false; await RefreshAsync(); }
    }
}

public sealed class FileOperationProgressWindow : Window
{
    private readonly CancellationTokenSource _cancel;
    private readonly TextBlock _status = new() { Text = "Подготавливаю безопасную операцию…", Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
    private bool _finished;
    public FileOperationProgressWindow(CancellationTokenSource cancel)
    {
        _cancel = cancel; Title = "Файловая операция"; Width = 520; Height = 200; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel(); panel.Children.Add(_status);
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 8, Margin = new Thickness(16) });
        var stop = new Button { Content = "Остановить на безопасной границе", Margin = new Thickness(16), Padding = new Thickness(8) };
        stop.Click += (_, _) => { _cancel.Cancel(); _status.Text = "Останавливаю. Дождитесь фиксации текущего шага."; stop.IsEnabled = false; };
        panel.Children.Add(stop); Content = panel;
    }
    public void SetProgress(int count) => _status.Text = $"Обработано: {count:N0}. После завершения доступны журнал и откат.";
    public void Finish() { _finished = true; Close(); }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished) { _cancel.Cancel(); e.Cancel = true; _status.Text = "Останавливаю на безопасной границе…"; }
        base.OnClosing(e);
    }
}
