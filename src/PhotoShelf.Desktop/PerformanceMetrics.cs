using System.Collections.Concurrent;
using PhotoShelf.Application.Background;
using PhotoShelf.Application.Diagnostics;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;

namespace PhotoShelf.Desktop;

// Local, bounded diagnostics. No media paths, filenames or image data are logged.
internal static class PerformanceMetrics
{
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private static DispatcherTimer? _heartbeat;
    private static System.Threading.Timer? _flush;
    private static DispatcherHeartbeatMonitor? _dispatcherHeartbeat;
    private static int _writing;
    private static double _lastCpuMs, _lastSampleMs;
    public static void Record(string metric, double milliseconds, long count = 0)
    {
        if (Pending.Count >= 512) return;
        Pending.Enqueue(string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:O},{metric},{milliseconds:F2},{count}"));
    }
    public static void Start(Dispatcher dispatcher)
    {
        _dispatcherHeartbeat = new();
        _heartbeat = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _heartbeat.Tick += (_, _) =>
        {
            Record("dispatcher.lateness", _dispatcherHeartbeat.Tick());
        };
        _heartbeat.Start();
        _flush = new System.Threading.Timer(_ => Flush(), null, 5000, 5000);
    }
    public static void Stop() { _heartbeat?.Stop(); _flush?.Dispose(); _ = Task.Run(Flush); }
    private static void Flush()
    {
        if (Interlocked.Exchange(ref _writing, 1) != 0) return;
        try
        {
            var dir = Path.Combine(LocalCatalogStore.DerivedDataDirectory, "diagnostics"); Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "performance.csv");
            if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
                File.Move(path, Path.Combine(dir, "performance.previous.csv"), true);
            var lines = new List<string>();
            while (Pending.TryDequeue(out var line)) lines.Add(line);
            using var process = Process.GetCurrentProcess();
            var now = Uptime.Elapsed.TotalMilliseconds;
            var cpu = process.TotalProcessorTime.TotalMilliseconds;
            var percent = _lastSampleMs <= 0 ? 0 : (cpu - _lastCpuMs) / Math.Max(1, now - _lastSampleMs) / Environment.ProcessorCount * 100;
            _lastCpuMs = cpu; _lastSampleMs = now;
            BackgroundWorkController.Shared.RecordSample(percent, process.WorkingSet64, _dispatcherHeartbeat?.TakeDelayMilliseconds() ?? 0,
                BackgroundWorkScheduler.Shared.PendingCount, BackgroundWorkScheduler.Shared.RunningCount);
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:O},process.working_set_mb,0,{process.WorkingSet64 / 1048576}"));
            File.AppendAllLines(path, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { Volatile.Write(ref _writing, 0); }
    }
}
