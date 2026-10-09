using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using PhotoShelf.Application.Duplicates;
using PhotoShelf.Application.Media;

namespace PhotoShelf.Desktop;

public sealed record MediaImageInfo(int Width, int Height, int FrameCount, SKEncodedImageFormat Format, long EncodedBytes)
{
    public long SourceFrameBytes => checked((long)Width * Height * 4);
    public bool IsAnimationFormat => Format is SKEncodedImageFormat.Gif or SKEncodedImageFormat.Webp;
}
public sealed record MediaDecodedFrame(BitmapSource Bitmap, int DelayMilliseconds);

public static class MediaBitmapLoader
{
    private static readonly HeifDecoderClient HeifDecoder = new();
    public const long MaximumSourceFrameBytes = 128L * 1024 * 1024;
    private const long MaximumAnimatedFileBytes = 128L * 1024 * 1024;

    public static MediaImageInfo? ReadInfo(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Detect content before creating the native codec: its constructor can parse frame tables.
        // Other formats stay on the WIC path, including files with a misleading GIF/WebP extension.
        if (!HasAnimationSignature(stream)) return null;
        if (stream.Length > MaximumAnimatedFileBytes)
            throw new NotSupportedException("Animated input exceeds the decoder input budget.");
        using var managed = new SKManagedStream(stream, disposeManagedStream: false);
        using var codec = SKCodec.Create(managed);
        if (codec is null) return null; // TIFF/legacy formats can use WIC.
        var info = codec.Info;
        var animationFormat = codec.EncodedFormat is SKEncodedImageFormat.Gif or SKEncodedImageFormat.Webp;
        return new(info.Width, info.Height, animationFormat ? Math.Max(1, codec.FrameCount) : 1, codec.EncodedFormat, stream.Length);
    }

    public static List<MediaDecodedFrame> ReadAnimation(string path, int maximumDimension, int maxFrames, CancellationToken token = default,
        MediaImageInfo? expectedInfo = null)
    {
        token.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDimension, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrames, 1);
        if (maxFrames > 128) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!HasAnimationSignature(stream)) throw new NotSupportedException("Animation decoder requires GIF or WebP content.");
        if (stream.Length > MaximumAnimatedFileBytes) throw new NotSupportedException("Animated input exceeds the decoder input budget.");
        using var managed = new SKManagedStream(stream, disposeManagedStream: false);
        using var codec = SKCodec.Create(managed) ?? throw new FileFormatException("Unsupported or corrupt image data.");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Gif or SKEncodedImageFormat.Webp))
            throw new NotSupportedException("Animation decoder requires GIF or WebP content.");
        var info = codec.Info;
        if (info.Width < 1 || info.Height < 1 || checked((long)info.Width * info.Height * 4) > MaximumSourceFrameBytes)
            throw new NotSupportedException("Animation canvas exceeds the decoder memory budget.");
        var frameCount = Math.Max(1, codec.FrameCount);
        if (expectedInfo is not null && (expectedInfo.Width != info.Width || expectedInfo.Height != info.Height ||
            expectedInfo.FrameCount != frameCount || expectedInfo.Format != codec.EncodedFormat || expectedInfo.EncodedBytes != stream.Length))
            throw new IOException("Image changed after its memory budget was reserved; retry the preview.");
        var count = Math.Min(frameCount, maxFrames);
        var result = new List<MediaDecodedFrame>(count);
        var timer = Stopwatch.StartNew();
        for (var frame = 0; frame < count; frame++)
        {
            token.ThrowIfCancellationRequested();
            // Bound work between native calls. A native codec call itself is not preemptible.
            if (frame > 0 && timer.Elapsed > TimeSpan.FromSeconds(5))
                return new() { result[0] };
            var bitmap = DecodeFrame(codec, maximumDimension, frame);
            var delay = codec.GetFrameInfo(frame, out var frameInfo) ? Math.Clamp(frameInfo.Duration, 20, 60_000) : 100;
            result.Add(new(bitmap, delay));
        }
        return result;
    }

    private static bool HasAnimationSignature(FileStream stream)
    {
        Span<byte> header = stackalloc byte[12];
        var count = 0;
        while (count < header.Length)
        {
            var read = stream.Read(header[count..]);
            if (read == 0) break;
            count += read;
        }
        stream.Position = 0;
        return (count >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))) ||
            (count == 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8));
    }

    private static BitmapSource DecodeFrame(SKCodec codec, int maximumDimension, int frame)
    {
        var original = codec.Info;
        var ratio = Math.Min(1d, maximumDimension / (double)Math.Max(original.Width, original.Height));
        var scaled = codec.GetScaledDimensions((float)ratio);
        using var colorSpace = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace);
        if (checked((long)info.Width * info.Height * 4) > MaximumSourceFrameBytes)
            throw new NotSupportedException("Decoded frame exceeds the memory budget.");
        using var bitmap = new SKBitmap(info);
        // PriorFrame=-1 asks Skia to compose all dependencies itself, including restore-previous/background disposal.
        var status = codec.GetPixels(info, bitmap.GetPixels(), bitmap.RowBytes, new SKCodecOptions(frame, -1));
        if (status != SKCodecResult.Success) throw new FileFormatException($"Image decode failed: {status}");
        if (bitmap.Width <= maximumDimension && bitmap.Height <= maximumDimension) return ToBitmapSource(bitmap);
        var width = Math.Max(1, (int)Math.Round(original.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(original.Height * ratio));
        using var resized = bitmap.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) ?? throw new FileFormatException("Image resize failed.");
        return ToBitmapSource(resized);
    }

    private static BitmapSource ToBitmapSource(SKBitmap image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null,
            image.GetPixels(), image.ByteCount, image.RowBytes);
        bitmap.Freeze();
        return bitmap;
    }

    internal static BitmapSource LoadWicBounded(string path, int maximumDimension)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var landscape = frame.PixelWidth >= frame.PixelHeight;
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (landscape) bitmap.DecodePixelWidth = maximumDimension; else bitmap.DecodePixelHeight = maximumDimension;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }

    public static bool IsHeif(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return HeifDecoderClient.HasHeifSignature(stream) ||
            Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif" or ".hif";
    }

    /// <summary>
    /// Decodes one bounded still frame and immediately reduces it to a platform-independent
    /// visual fingerprint. The full-resolution source is never retained by the indexer.
    /// </summary>
    public static PerceptualFingerprint LoadPerceptualFingerprint(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // GIF/WebP use the bundled decoder, including still WebP and disguised extensions.
        // Fingerprints use one frame within the same input and pixel limits as previews.
        var animationInfo = ReadInfo(path);
        var bitmap = animationInfo is not null
            ? ReadAnimation(path, 64, 1, token, animationInfo)[0].Bitmap
            : LoadStillBounded(path, 64, token);
        var gray = bitmap.Format == PixelFormats.Gray8
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
        token.ThrowIfCancellationRequested();
        var stride = gray.PixelWidth;
        var pixels = new byte[checked(stride * gray.PixelHeight)];
        gray.CopyPixels(pixels, stride, 0);
        token.ThrowIfCancellationRequested();
        return PerceptualFingerprintAlgorithm.Compute(pixels, gray.PixelWidth, gray.PixelHeight, stride);
    }

    /// <summary>Called only by background decoders. HEIC never depends on a Windows WIC extension.</summary>
    public static BitmapSource LoadStillBounded(string path, int maximumDimension, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!IsHeif(path)) return LoadWicBounded(path, maximumDimension);
        var frame = HeifDecoder.DecodeAsync(path, maximumDimension, token).GetAwaiter().GetResult();
        // WPF uses BGRA; swap in place so the conversion stays inside the reserved pixel budget.
        for (var offset = 0; offset < frame.Pixels.Length; offset += 4)
            (frame.Pixels[offset], frame.Pixels[offset + 2]) = (frame.Pixels[offset + 2], frame.Pixels[offset]);
        token.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32,
            null, frame.Pixels, frame.Stride);
        bitmap.Freeze();
        return bitmap;
    }
}
