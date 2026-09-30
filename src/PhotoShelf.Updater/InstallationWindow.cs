using PhotoShelf.Application.Updates;
using PhotoShelf.Application.Updates.Installation;

namespace PhotoShelf.Updater;

/// <summary>A separate responsive window remains visible after PhotoShelf has drained and closed.</summary>
internal sealed class InstallationWindow : Form
{
    private readonly UpdateInstallationPaths _paths;
    private readonly string _currentVersion;
    private readonly string _requestPath;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Label _stage = new() { AutoSize = false, Dock = DockStyle.Top, Height = 90,
        Text = "Подготавливаю обновление…", Padding = new Padding(0, 8, 0, 0) };
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
        Text = "Обновление устанавливается в отдельную папку. Фото, видео и каталог остаются на месте." };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 20, Style = ProgressBarStyle.Marquee };
    private readonly Button _button = new() { Text = "Отмена", AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
    private bool _finished;
    private bool _activationStarted;
    public int ExitCode { get; private set; } = 1;

    public InstallationWindow(UpdateInstallationPaths paths, string currentVersion, string requestPath)
    {
        _paths = paths; _currentVersion = currentVersion; _requestPath = requestPath;
        Text = "Обновление PhotoShelf Ultra";
        ClientSize = new Size(550, 280);
        MinimumSize = new Size(480, 290);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Padding = new Padding(20);
        Font = new Font("Segoe UI", 10);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        bottom.Controls.Add(_button);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };
        body.Controls.Add(_details);
        Controls.Add(body); Controls.Add(bottom); Controls.Add(_progress); Controls.Add(_stage);
        _button.Click += (_, _) => { if (_finished) Close(); else RequestCancellation(); };
        FormClosing += (_, args) =>
        {
            if (_finished) return;
            args.Cancel = true;
            RequestCancellation();
        };
        Shown += async (_, _) => await RunInstallationAsync();
    }

    private async Task RunInstallationAsync()
    {
        try
        {
            var options = new UpdateInstallationOptions { Progress = new Progress<string>(ShowStage) };
            var installer = new UpdateInstaller(_paths, UpdateTrust.PublicKeyPem, _currentVersion, options: options);
            var result = await installer.InstallAsync(_requestPath, _cancellation.Token);
            ExitCode = 0;
            _finished = true;
            if (result.StartupReady) { Close(); return; }
            _stage.Text = "Обновление установлено. PhotoShelf ещё запускается.";
            _details.Text = "Запуск библиотеки пока не подтверждён. Можно закрыть это окно и дождаться PhotoShelf. " +
                "Автоматический откат программы и каталога не выполняется.";
        }
        catch (OperationCanceledException)
        {
            _stage.Text = "Подготовка обновления отменена.";
            _details.Text = "Прежняя программа, каталог и оригиналы сохранены. Запустите PhotoShelf обычным способом. " +
                "Для новой попытки снова подтвердите установку в настройках.";
        }
        catch (Exception error)
        {
            _stage.Text = _activationStarted ? "Нужно проверить запуск обновлённой программы." : "Обновление остановлено.";
            _details.Text = (_activationStarted ? "Обновлённая версия сохранена; автоматический откат каталога не выполнялся. " :
                "Прежняя установка и оригиналы сохранены. Повторная установка требует вашего подтверждения. ") +
                "\n\n" + error.Message;
        }
        _finished = true;
        _progress.Style = ProgressBarStyle.Blocks;
        _button.Text = "Закрыть";
        _button.Enabled = true;
    }

    private void ShowStage(string stage)
    {
        if (IsDisposed) return;
        Console.WriteLine("PhotoShelf update: " + stage);
        if (stage is "ready-to-activate" or "activated" or "application-started")
        { _activationStarted = true; _button.Enabled = false; }
        _stage.Text = stage switch
        {
            "waiting-for-parent" => "Ожидаю завершения работы PhotoShelf…",
            "verifying-stage" => "Проверяю подпись и целостность обновления…",
            "copying-program" => "Копирую новую версию в отдельную папку…",
            "checking-isolated-startup" => "Проверяю запуск программы на временных данных…",
            "ready-to-activate" => "Подключаю проверенную версию…",
            "activated" => "Новая версия подключена…",
            "application-started" => "Ожидаю открытия библиотеки PhotoShelf…",
            "startup-ready" => "PhotoShelf обновлён и готов к работе.",
            "startup-not-yet-confirmed" => "Обновление установлено. PhotoShelf ещё запускается…",
            _ => _stage.Text
        };
    }

    private void RequestCancellation()
    {
        if (_activationStarted || _cancellation.IsCancellationRequested) return;
        _button.Enabled = false;
        _stage.Text = "Отменяю подготовку на безопасной границе…";
        _cancellation.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cancellation.Dispose();
        base.Dispose(disposing);
    }
}
