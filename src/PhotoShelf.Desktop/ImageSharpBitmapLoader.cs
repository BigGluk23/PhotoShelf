using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoShelf.Desktop;

public static class ImageSharpBitmapLoader
{
    public static ImageSource? TryLoad(string path, int decodeWidth)
    {
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(path);
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

    private static BitmapSource ToBitmapSource(Image<Rgba32> image)
    {
        var width = image.Width;
        var height = image.Height;
        var rgba = new byte[width * height * 4];
        image.CopyPixelDataTo(rgba);
        var pixels = new byte[rgba.Length];
        for (var index = 0; index < rgba.Length; index += 4)
        {
            pixels[index] = rgba[index + 2];
            pixels[index + 1] = rgba[index + 1];
            pixels[index + 2] = rgba[index];
            pixels[index + 3] = rgba[index + 3];
        }

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
