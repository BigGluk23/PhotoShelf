using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoShelf.Desktop;

// Each realized image owns its cancellation and releases frames when recycled/unloaded.
public static class AsyncMediaImage
{
    private static readonly SemaphoreSlim DecodeGate = new(2, 2);
    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached("Path", typeof(string), typeof(AsyncMediaImage), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached("DecodeWidth", typeof(int), typeof(AsyncMediaImage), new PropertyMetadata(256));
    public static readonly DependencyProperty StatusProperty = DependencyProperty.RegisterAttached("Status", typeof(string), typeof(AsyncMediaImage), new PropertyMetadata(""));
    public static string GetStatus(DependencyObject obj) => (string)obj.GetValue(StatusProperty);
    public static void SetStatus(DependencyObject obj, string value) => obj.SetValue(StatusProperty, value);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(AsyncMediaImage));
    public static string? GetPath(DependencyObject obj) => (string?)obj.GetValue(PathProperty);
    public static void SetPath(DependencyObject obj, string? value) => obj.SetValue(PathProperty, value);
    public static int GetDecodeWidth(DependencyObject obj) => (int)obj.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject obj, int value) => obj.SetValue(DecodeWidthProperty, value);
    private sealed class State
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public DispatcherTimer? Timer { get; set; }
        public void Stop() { Cancellation.Cancel(); Timer?.Stop(); }
    }
    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        if (obj is not System.Windows.Controls.Image image) return;
        image.Loaded -= Loaded; image.Unloaded -= Unloaded;
        image.Loaded += Loaded; image.Unloaded += Unloaded;
        Stop(image);
        if (image.IsLoaded) Start(image);
    }
    private static void Loaded(object sender, RoutedEventArgs e) => Start((System.Windows.Controls.Image)sender);
    private static void Unloaded(object sender, RoutedEventArgs e) => Stop((System.Windows.Controls.Image)sender);
    private static void Stop(System.Windows.Controls.Image image)
    {
        (image.GetValue(StateProperty) as State)?.Stop();
        image.ClearValue(StateProperty);
        image.Source = null;
        SetStatus(image, "");
    }
    private static async void Start(System.Windows.Controls.Image image)
    {
        Stop(image);
        if (GetPath(image) is not { Length: > 0 } path) return;
        var state = new State(); image.SetValue(StateProperty, state);
        var token = state.Cancellation.Token;
        var width = GetDecodeWidth(image);
        try
        {
            var frames = await Task.Run(async () =>
            {
                await DecodeGate.WaitAsync(token);
                try { token.ThrowIfCancellationRequested(); return Decode(path, width, token); }
                finally { DecodeGate.Release(); }
            }, token);
            if (token.IsCancellationRequested || frames.Count == 0) return;
            image.Source = frames[0].Bitmap;
            image.ToolTip = frames[0].Notice;
            SetStatus(image, frames[0].Notice ?? "");
            if (frames.Count <= 1) return;
            var index = 0;
            state.Timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(frames[0].Delay) };
            state.Timer.Tick += (_, _) =>
            {
                index = (index + 1) % frames.Count;
                image.Source = frames[index].Bitmap;
                state.Timer.Interval = TimeSpan.FromMilliseconds(frames[index].Delay);
            };
            state.Timer.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) { image.ToolTip = $"Не удалось открыть: {ex.Message}"; SetStatus(image, "Нет встроенного декодера или файл недоступен"); } }
    }
    private sealed record Frame(BitmapSource Bitmap, int Delay, string? Notice = null);
    private static List<Frame> Decode(string path, int width, CancellationToken token)
    {
        if (PhotoItem.IsVideoPath(path))
        {
            var bitmap = ThumbnailCache.LoadOrCreate(path, width, static (p, w) => VideoThumbnailProvider.TryLoad(p, w)) as BitmapSource;
            return bitmap is null ? new() : new() { new(bitmap, 100) };
        }
        if (PhotoItem.IsAnimatedGifPath(path) || PhotoItem.IsWebpPath(path))
        {
            var info = SixLabors.ImageSharp.Image.Identify(path);
            var scale = Math.Min(1d, width / (double)Math.Max(info.Width, info.Height));
            var frameBytes = (long)Math.Max(1, info.Width * scale) * (long)Math.Max(1, info.Height * scale) * 4;
            var allowAnimation = frameBytes * info.FrameMetadataCollection.Count <= 64L * 1024 * 1024 && info.FrameMetadataCollection.Count < 256;
            using var decoded = SixLabors.ImageSharp.Image.Load<Rgba32>(new DecoderOptions { TargetSize = new SixLabors.ImageSharp.Size(width, width), MaxFrames = allowAnimation ? 256u : 1u }, path);
            // Bound retained animation memory, while keeping an explicit first-frame fallback.
            var animate = allowAnimation && (long)decoded.Width * decoded.Height * 4 * decoded.Frames.Count <= 64L * 1024 * 1024 && decoded.Frames.Count < 256;
            var count = animate ? decoded.Frames.Count : 1;
            var frames = new List<Frame>();
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                using var frame = decoded.Frames.CloneFrame(i);
                var delay = PhotoItem.IsAnimatedGifPath(path) ? decoded.Frames[i].Metadata.GetGifMetadata().FrameDelay * 10
                    : (int)decoded.Frames[i].Metadata.GetWebpMetadata().FrameDelay;
                frames.Add(new(ImageSharpBitmapLoader.ToBitmapSource(frame), Math.Max(20, delay), animate ? null : "Большая анимация: показан первый кадр"));
            }
            return frames;
        }
        if (width <= 512)
        {
            var cached = ThumbnailCache.LoadOrCreate(path, width, static (p, w) => LoadStill(p, w)) as BitmapSource;
            return cached is null ? new() : new() { new(cached, 100) };
        }
        return new() { new(LoadStill(path, width), 100) };
    }
    private static BitmapSource LoadStill(string path, int width)
    {
        var result = new BitmapImage();
        result.BeginInit(); result.CacheOption = BitmapCacheOption.OnLoad;
        result.DecodePixelWidth = width; result.UriSource = new Uri(path, UriKind.Absolute);
        result.EndInit(); result.Freeze();
        return result;
    }
}
