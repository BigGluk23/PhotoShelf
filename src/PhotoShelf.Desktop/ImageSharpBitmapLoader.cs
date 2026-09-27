using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Processing;

namespace PhotoShelf.Desktop;

public static class ImageSharpBitmapLoader
{
    internal static Configuration DecodeConfiguration { get; } = CreateConfiguration();
    private static Configuration CreateConfiguration()
    {
        var config = Configuration.Default.Clone();
        config.MaxDegreeOfParallelism = 2;
        config.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions { MaximumPoolSizeMegabytes = 16, AllocationLimitMegabytes = 128 });
        return config;
    }
    internal static BitmapSource LoadWicBounded(string path, int maximumDimension)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var landscape = frame.PixelWidth >= frame.PixelHeight;
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (landscape) bitmap.DecodePixelWidth = maximumDimension; else bitmap.DecodePixelHeight = maximumDimension;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static ImageSource? TryLoad(string path, int decodeWidth)
    {
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(new DecoderOptions { Configuration = DecodeConfiguration, TargetSize = new SixLabors.ImageSharp.Size(Math.Max(1, decodeWidth), Math.Max(1, decodeWidth)), MaxFrames = 1 }, path);
            if (decodeWidth > 0 && image.Width > decodeWidth)
            {
                var height = Math.Max(1, (int)Math.Round(image.Height * (decodeWidth / (double)image.Width)));
                image.Mutate(context => context.Resize(decodeWidth, height));
            }

            return ToBitmapSource(image);
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource ToBitmapSource(Image<Rgba32> image)
    {
        var width = image.Width;
        var height = image.Height;
        var pixels = new byte[checked(width * height * 4)];
        image.CopyPixelDataTo(pixels);
        for (var index = 0; index < pixels.Length; index += 4)
            (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
