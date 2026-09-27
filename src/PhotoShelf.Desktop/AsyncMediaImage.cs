using System.IO;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoShelf.Application.Background;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using WpfImage = System.Windows.Controls.Image;

namespace PhotoShelf.Desktop;

/// <summary>Decoded frames belong to a realized image; recycling releases their shared reservation and cancels stale work.</summary>
public static class AsyncMediaImage
{
    private static readonly MemoryBudget DecodedBudget = new(256L * 1024 * 1024);
    private static readonly ConditionalWeakTable<WpfImage, object> RealizedImages = new();
    private static readonly ConcurrentDictionary<State, byte> PendingDecodes = new();
    private static readonly List<PauseLease> Pauses = new();
    public static long ReservedDecodedBytes => DecodedBudget.Used;

    /// <summary>
    /// Call and dispose the returned lease on the owning WPF UI thread. Cancels previews and asynchronously
    /// waits until all actual decode delegates return and close their streams, including recycled images.
    /// Paths must be absolute; null pauses all images. Matching paths compare without case on Windows.
    /// Newly loaded affected images defer decoding until their last applicable lease is disposed.
    /// </summary>
    public static async Task<IDisposable> PauseForFileOperationsAsync(IReadOnlySet<string>? affectedPaths = null)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        System.Windows.Application.Current?.Dispatcher.VerifyAccess();
        var lease = new PauseLease(dispatcher, affectedPaths);
        var images = RealizedImages.Select(pair => pair.Key).Where(image => lease.Matches(GetPath(image))).ToArray();
        foreach (var image in images) image.Dispatcher.VerifyAccess();
        Pauses.Add(lease);
        try
        {
            foreach (var image in images) Stop(image);
            // Stop already-detached/recycled decodes too: they can still be inside a synchronous codec.
            var pending = PendingDecodes.Keys.Where(state => lease.Matches(state.Path)).ToArray();
            foreach (var state in pending) state.Cancellation.Cancel();
            await Task.WhenAll(pending.Select(state => state.DecodeFinished.Task));
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static bool IsPaused(string? path) => Pauses.Any(pause => pause.Matches(path));
    private sealed class PauseLease : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly HashSet<string>? _affectedPaths;
        private bool _disposed;
        public PauseLease(Dispatcher dispatcher, IReadOnlySet<string>? affectedPaths)
        {
            _dispatcher = dispatcher;
            _affectedPaths = affectedPaths is null ? null : new HashSet<string>(affectedPaths,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }
        public bool Matches(string? path) => path is not null && (_affectedPaths is null || _affectedPaths.Contains(path));
        public void Dispose()
        {
            _dispatcher.VerifyAccess();
            if (_disposed) return;
            _disposed = true;
            Pauses.Remove(this);
            foreach (var pair in RealizedImages.ToArray())
            {
                var image = pair.Key;
                var path = GetPath(image);
                if (Matches(path) && !IsPaused(path) && image.IsLoaded && image.IsVisible) Start(image);
            }
        }
    }
    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached("Path", typeof(string), typeof(AsyncMediaImage), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached("DecodeWidth", typeof(int), typeof(AsyncMediaImage), new PropertyMetadata(256, Changed));
    public static readonly DependencyProperty StatusProperty = DependencyProperty.RegisterAttached("Status", typeof(string), typeof(AsyncMediaImage), new PropertyMetadata(""));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(AsyncMediaImage));
    public static string GetStatus(DependencyObject obj) => (string)obj.GetValue(StatusProperty);
    public static void SetStatus(DependencyObject obj, string value) => obj.SetValue(StatusProperty, value);
    public static string? GetPath(DependencyObject obj) => (string?)obj.GetValue(PathProperty);
    public static void SetPath(DependencyObject obj, string? value) => obj.SetValue(PathProperty, value);
    public static int GetDecodeWidth(DependencyObject obj) => (int)obj.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject obj, int value) => obj.SetValue(DecodeWidthProperty, value);

    private sealed class State(string path)
    {
        public string Path { get; } = path;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource DecodeFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DispatcherTimer? Timer { get; set; }
        public DecodedFrames? Frames { get; set; }
        public void Stop()
        {
            Cancellation.Cancel();
            Timer?.Stop();
            Timer = null;
            Frames?.Dispose();
            Frames = null;
        }
    }
    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        if (obj is not WpfImage image) return;
        image.Loaded -= Loaded; image.Unloaded -= Unloaded; image.IsVisibleChanged -= VisibilityChanged;
        image.Loaded += Loaded; image.Unloaded += Unloaded; image.IsVisibleChanged += VisibilityChanged;
        Stop(image);
        if (image.IsLoaded) Start(image);
    }
    private static void Loaded(object sender, RoutedEventArgs e) => Start((WpfImage)sender);
    private static void Unloaded(object sender, RoutedEventArgs e)
    {
        var image = (WpfImage)sender;
        Stop(image);
        RealizedImages.Remove(image);
    }
    private static void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var image = (WpfImage)sender;
        if (!image.IsVisible) Stop(image);
        else if (image.IsLoaded) Start(image);
    }
    private static void Stop(WpfImage image)
    {
        image.Source = null;
        (image.GetValue(StateProperty) as State)?.Stop();
        image.ClearValue(StateProperty);
        image.ToolTip = null;
        SetStatus(image, "");
    }
    private static async void Start(WpfImage image)
    {
        Stop(image);
        if (!image.IsLoaded || GetPath(image) is not { Length: > 0 } path)
        {
            RealizedImages.Remove(image);
            return;
        }
        RealizedImages.GetValue(image, _ => new object());
        if (!image.IsVisible || IsPaused(path)) return;
        var state = new State(path);
        PendingDecodes.TryAdd(state, 0);
        image.SetValue(StateProperty, state);
        var token = state.Cancellation.Token;
        var width = Math.Clamp(GetDecodeWidth(image), 32, 4096);
        DecodedFrames? result = null;
        try
        {
            result = await BackgroundWorkScheduler.Shared.RunAsync(BackgroundWorkPriority.VisiblePreview, ct => Decode(path, width, ct), token);
            if (token.IsCancellationRequested || !image.IsLoaded || !ReferenceEquals(image.GetValue(StateProperty), state)) return;
            state.Frames = result;
            result = null; // Ownership transfers only after checking the realized element's identity.
            var frames = state.Frames.Frames;
            if (frames.Count == 0) return;
            image.Source = frames[0].Bitmap;
            image.ToolTip = frames[0].Notice;
            SetStatus(image, frames[0].Notice ?? "");
            if (frames.Count <= 1) return;
            var index = 0;
            state.Timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(frames[0].Delay) };
            state.Timer.Tick += (_, _) =>
            {
                if (!image.IsLoaded || !ReferenceEquals(image.GetValue(StateProperty), state)) { state.Stop(); return; }
                index = (index + 1) % frames.Count;
                image.Source = frames[index].Bitmap;
                state.Timer!.Interval = TimeSpan.FromMilliseconds(frames[index].Delay);
            };
            state.Timer.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(image.GetValue(StateProperty), state))
            {
                image.ToolTip = $"Не удалось открыть: {ex.Message}";
                SetStatus(image, ex is MediaBudgetException ? "Превью ограничено общим бюджетом памяти" : "Нет встроенного декодера или файл недоступен");
            }
        }
        finally
        {
            result?.Dispose();
            PendingDecodes.TryRemove(state, out _);
            state.DecodeFinished.TrySetResult();
        }
    }

    private sealed record Frame(BitmapSource Bitmap, int Delay, string? Notice = null);
    private sealed class DecodedFrames(List<Frame> frames, MemoryBudget.Lease reservation) : IDisposable
    {
        public List<Frame> Frames { get; } = frames;
        public void Dispose() { Frames.Clear(); reservation.Dispose(); }
    }
    private sealed class MediaBudgetException : Exception { }

    private static DecodedFrames Decode(string path, int width, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var video = PhotoItem.IsVideoPath(path);
        var animatedFormat = PhotoItem.IsAnimatedGifPath(path) || Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase);
        // Read headers in a worker. Bounding frame dimensions also bounds portrait/panoramic images.
        var frameCount = 1;
        long sourceFrameBytes = 0;
        if (animatedFormat)
        {
            var info = SixLabors.ImageSharp.Image.Identify(new DecoderOptions { Configuration = ImageSharpBitmapLoader.DecodeConfiguration, MaxFrames = 129 }, path);
            frameCount = Math.Max(1, info.FrameMetadataCollection.Count);
            sourceFrameBytes = checked((long)info.Width * info.Height * 4);
            if (sourceFrameBytes > 128L * 1024 * 1024) throw new MediaBudgetException();
        }
        var animate = animatedFormat && frameCount is > 1 and <= 128 && (long)width * width * 4 * frameCount <= 64L * 1024 * 1024;
        var count = animate ? frameCount : 1;
        // Budget includes decoded source frames + retained WPF frames + conversion scratch space.
        long RequiredBytes(int size, int frames) => checked(sourceFrameBytes * frames + (long)size * size * 4 * (frames + 3));
        var reservation = DecodedBudget.TryReserve(RequiredBytes(width, count));
        if (reservation is null && animate)
        {
            animate = false; count = 1;
            reservation = DecodedBudget.TryReserve(RequiredBytes(width, 1));
        }
        while (reservation is null && width > 64)
        {
            width /= 2;
            reservation = DecodedBudget.TryReserve(RequiredBytes(width, 1));
        }
        if (reservation is null) throw new MediaBudgetException();
        try
        {
            var frames = new List<Frame>();
            if (video)
            {
                var bitmap = ThumbnailCache.LoadOrCreate(path, width, static (p, w) => VideoThumbnailProvider.TryLoad(p, w)) as BitmapSource;
                if (bitmap is not null) frames.Add(new(bitmap, 100));
            }
            else if (animatedFormat)
            {
                using var decoded = SixLabors.ImageSharp.Image.Load<Rgba32>(new DecoderOptions
                {
                    Configuration = ImageSharpBitmapLoader.DecodeConfiguration,
                    TargetSize = new SixLabors.ImageSharp.Size(width, width), MaxFrames = animate ? (uint)frameCount : 1u
                }, path);
                if (decoded.Width > width || decoded.Height > width)
                    decoded.Mutate(context => context.Resize(new ResizeOptions { Size = new SixLabors.ImageSharp.Size(width, width), Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max }));
                for (var i = 0; i < decoded.Frames.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    using var frame = decoded.Frames.CloneFrame(i);
                    var delay = PhotoItem.IsAnimatedGifPath(path) ? decoded.Frames[i].Metadata.GetGifMetadata().FrameDelay * 10
                        : (int)decoded.Frames[i].Metadata.GetWebpMetadata().FrameDelay;
                    frames.Add(new(ImageSharpBitmapLoader.ToBitmapSource(frame), Math.Max(20, delay), !animate && frameCount > 1 ? "Анимация ограничена бюджетом памяти: показан первый кадр" : null));
                }
            }
            else
            {
                var bitmap = width <= 512 ? ThumbnailCache.LoadOrCreate(path, width, static (p, w) => LoadStill(p, w)) as BitmapSource : LoadStill(path, width);
                if (bitmap is not null) frames.Add(new(bitmap, 100));
            }
            token.ThrowIfCancellationRequested();
            var retainedBytes = frames.Sum(frame => (long)frame.Bitmap.PixelWidth * frame.Bitmap.PixelHeight * Math.Max(4, (frame.Bitmap.Format.BitsPerPixel + 7) / 8));
            if (!reservation.TryResize(retainedBytes)) throw new MediaBudgetException();
            return new(frames, reservation);
        }
        catch { reservation.Dispose(); throw; }
    }
    private static BitmapSource LoadStill(string path, int width) => ImageSharpBitmapLoader.LoadWicBounded(path, width);
}
