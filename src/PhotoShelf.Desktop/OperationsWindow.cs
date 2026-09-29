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
    private readonly Button _reviewLegacy = new() { Content = "Проверить старый журнал", Margin = new Thickness(6), Padding = new Thickness(8) };
    private CancellationTokenSource? _reviewCancellation;
    private bool _busy;
    private int _revision;
    public OperationsWindow(string directory, Func<MoveOperationHistory, bool, Task> execute, Func<Task<string>> backup,
        Func<BackgroundActivitySnapshot>? background = null, Func<Task>? toggleBackground = null)
    {
        _directory = directory; _execute = execute;
        Title = "Операции и восстановление"; Width = 1100; Height = 720; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(10) }; Content = grid;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _status.Text = "Выберите операцию. Внизу показаны исходные и целевые пути. При откате направление меняется. Совпадения и изменённые файлы не перезаписываются.";
        var header = new StackPanel(); grid.Children.Add(header);
        if (background is not null && toggleBackground is not null)
        {
            var title = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4) };
            var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4) };
            var toggle = new Button { Margin = new Thickness(8), Padding = new Thickness(8), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            header.Children.Add(title);
            header.Children.Add(new ScrollViewer { Content = details, MaxHeight = 175, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            header.Children.Add(toggle);
            void RefreshBackground()
            {
                var state = background(); title.Text = state.Summary; details.Text = state.Details;
                toggle.Content = state.Paused ? "Продолжить фоновую обработку" : "Пауза индексирования и автообновления";
                toggle.IsEnabled = !_busy && state.CanToggle;
            }
            toggle.Click += async (_, _) =>
            {
                if (_busy) return;
                _busy = true; SetButtons(); toggle.IsEnabled = false;
                try { await toggleBackground(); }
                catch (Exception ex) { _status.Text = $"Не удалось изменить режим: {ex.Message}"; }
                finally { _busy = false; SetButtons(); RefreshBackground(); }
            };
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) => RefreshBackground();
            Loaded += (_, _) => { RefreshBackground(); timer.Start(); };
            Closed += (_, _) => timer.Stop();
        }
        header.Children.Add(new TextBlock { Text = "История переносов и восстановления", FontWeight = FontWeights.SemiBold, Margin = new Thickness(8) });
        header.Children.Add(_status);
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
        AddColumn(_files, "Проверка", nameof(LegacyJournalFile.Problem), 330);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_recover); buttons.Children.Add(_undo);
        buttons.Children.Add(_export);
        buttons.Children.Add(_reviewLegacy);
        _reviewLegacy.Click += async (_, _) => await ReviewLegacyAsync();
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
        Closing += (_, e) => { if (_busy) { _reviewCancellation?.Cancel(); e.Cancel = true; } };
    }
    private static void AddColumn(DataGrid grid, string title, string property, double width) =>
        grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = width });
    private async Task RefreshAsync()
    {
        try
        {
            _history.ItemsSource = await Task.Run(() => FileOperations.CreateService().ReadHistory(_directory));
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
            if (operation.IsLegacy)
            {
                _files.ItemsSource = null;
                _status.Text = operation.LegacyReviewed ? "Старый перенос проверен. Автоматический откат для этого формата недоступен." : "Нажмите «Проверить старый журнал». Будут прочитаны файлы назначения и сверены их SHA-256; файлы не перемещаются. Закрытие окна отменяет проверку.";
                return;
            }
            var plan = await Task.Run(() => FileOperations.CreateService().ReadPlan(operation.JournalPath));
            if (revision == _revision) _files.ItemsSource = plan;
        }
        catch (Exception ex) { _files.ItemsSource = null; _status.Text = $"Автоматическое восстановление недоступно: {ex.Message}"; }
    }
    private void SetButtons()
    {
        var operation = _history.SelectedItem as MoveOperationHistory;
        _recover.IsEnabled = !_busy && operation is { IsLegacy: false } && (operation.PendingFiles > 0 || operation.HasRecoverableTail);
        _undo.IsEnabled = !_busy && operation?.CanUndo == true;
        _reviewLegacy.IsEnabled = !_busy && operation is { IsLegacy: true, LegacyReviewed: false };
        _export.IsEnabled = !_busy && operation is not null;
        _history.IsEnabled = !_busy;
    }
    private async Task ReviewLegacyAsync()
    {
        if (_busy || _history.SelectedItem is not MoveOperationHistory { IsLegacy: true } operation) return;
        _busy = true; SetButtons();
        using var cancellation = new CancellationTokenSource();
        _reviewCancellation = cancellation;
        try
        {
            _status.Text = "Проверяю файлы старой операции по SHA-256. Закрытие окна отменяет проверку…";
            var review = await LegacyJournalReviewService.ReviewAsync(operation.JournalPath, cancellation.Token);
            _files.ItemsSource = review.Files;
            if (!review.CanAcknowledge)
            {
                _status.Text = "Есть неподтверждённые файлы. Блокировка сохранена; смотрите причины в таблице. Сохраните журнал для ручного разбора. Не удаляйте оригиналы, журналы и временные файлы.";
                return;
            }
            _status.Text = $"Проверены {review.Files.Count:N0} файлов. Назначения совпадают с хэшами; исходные и временные пути свободны.";
            if (System.Windows.MessageBox.Show(this, "Все завершённые переносы этого старого журнала проверены. Записать результат проверки и снять блокировку для этой операции? Медиа и старый журнал останутся без изменений. Автоматический откат старого формата недоступен.",
                "Подтвердить результат проверки", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            await LegacyJournalReviewService.AcknowledgeCompletedAsync(operation.JournalPath, cancellation.Token);
            await RefreshAsync();
            _status.Text = "Результат проверки сохранён отдельным файлом. Старый журнал и медиа сохранены.";
        }
        catch (OperationCanceledException) { _status.Text = "Проверка отменена; блокировка сохранена."; }
        catch (Exception ex) { _status.Text = "Проверка не завершена: " + ex.Message; }
        finally { _reviewCancellation = null; _busy = false; SetButtons(); }
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
