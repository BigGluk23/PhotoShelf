using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PhotoShelf.Application.Background;
using Forms = System.Windows.Forms;

namespace PhotoShelf.Desktop;

public partial class MainWindow
{
    private void InitializeBackgroundEnvironment()
    {
        RefreshBackgroundPower();
        InputManager.Current.PreProcessInput += OnBackgroundInput;
        SystemEvents.PowerModeChanged += OnBackgroundPowerChanged;
        Closed += (_, _) =>
        {
            InputManager.Current.PreProcessInput -= OnBackgroundInput;
            SystemEvents.PowerModeChanged -= OnBackgroundPowerChanged;
            BackgroundWorkController.Shared.SetSuspended(false);
        };
    }
    private void OnBackgroundInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is MouseWheelEventArgs or MouseButtonEventArgs or System.Windows.Input.KeyEventArgs)
            BackgroundWorkController.Shared.NoteInteraction();
    }
    private void OnBackgroundPowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        // Static system events can arrive off Dispatcher. This only changes a thread-safe
        // pacing policy; no file, catalog, cancellation or recovery operation is started.
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
            BackgroundWorkController.Shared.SetSuspended(e.Mode == PowerModes.Suspend);
        if (e.Mode != PowerModes.Suspend && !Dispatcher.HasShutdownStarted)
            _ = Dispatcher.BeginInvoke(RefreshBackgroundPower, DispatcherPriority.Background);
    }
    private static void RefreshBackgroundPower() => BackgroundWorkController.Shared.SetPowerState(
        Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Offline);

    private static string BackgroundLoadDetails()
    {
        var state = BackgroundWorkController.Shared.Snapshot(BackgroundWorkScheduler.Shared.PendingCount,
            BackgroundWorkScheduler.Shared.RunningCount);
        var mode = state.Mode switch { BackgroundLoadMode.Quiet => "тихий", BackgroundLoadMode.Fast => "максимальная скорость", _ => "сбалансированный" };
        var reason = state.Suspended ? "ожидание пробуждения" : state.OnBattery ? "экономия батареи" : state.UserActive ? "приоритет просмотра" : "обычная работа";
        var lines = new List<string> { $"Нагрузка: {mode}; {reason}. Очередь чтений: {state.Pending}; выполняется: {state.Running}." };
        foreach (var task in state.Tasks)
        {
            var name = task.Kind switch { BackgroundTaskKind.Scan => "Обход папок", BackgroundTaskKind.Metadata => "Метаданные", _ => "Визуальный индекс" };
            var phase = task.Phase switch
            {
                BackgroundTaskPhase.WaitingForReader => "жду завершения предыдущего читателя",
                BackgroundTaskPhase.Preparing => "подготовка очереди", BackgroundTaskPhase.Queued => "ожидание очереди чтения", BackgroundTaskPhase.Reading => "чтение",
                BackgroundTaskPhase.Saving => "запись в каталог", BackgroundTaskPhase.Pacing => "снижаю нагрузку",
                BackgroundTaskPhase.Completed => "готово", BackgroundTaskPhase.Cancelled => "остановлено", _ => "ошибка"
            };
            lines.Add($"{name}: {phase}; проверено: {task.Completed:N0}; ошибок: {task.Errors:N0}; {task.ItemsPerSecond:F1} записей/с; без результата: {task.SinceProgressSeconds:N0} с.");
            if (task.LongRunningRead) lines.Add("Чтение длится больше минуты. Возможен медленный или недоступный носитель. Читатель ещё активен; безопасная остановка ждёт его завершения.");
        }
        return string.Join("\n", lines);
    }
}
