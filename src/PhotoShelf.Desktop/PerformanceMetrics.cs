using System.Collections.Concurrent;
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
    private static long _lastTick;
    private static int _writing;
    public static void Record(string metric, double milliseconds, long count = 0)
    {
        if (Pending.Count >= 512) return;
        Pending.Enqueue(string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:O},{metric},{milliseconds:F2},{count}"));
    }
    public static void Start(Dispatcher dispatcher)
    {
        _lastTick = Uptime.ElapsedMilliseconds;
        _heartbeat = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _heartbeat.Tick += (_, _) =>
        {
            var now = Uptime.ElapsedMilliseconds;
            Record("dispatcher.lateness", Math.Max(0, now - _lastTick - 100)); _lastTick = now;
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
            var dir = Path.Combine(LocalCatalogStore.CatalogDirectory, "diagnostics"); Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "performance.csv");
            if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
                File.Move(path, Path.Combine(dir, "performance.previous.csv"), true);
            var lines = new List<string>();
            while (Pending.TryDequeue(out var line)) lines.Add(line);
            using var process = Process.GetCurrentProcess();
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:O},process.working_set_mb,0,{process.WorkingSet64 / 1048576}"));
            File.AppendAllLines(path, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { Volatile.Write(ref _writing, 0); }
    }
}
