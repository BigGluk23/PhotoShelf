using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoShelf.Desktop;

public static class VideoThumbnailProvider
{
    private static readonly Guid ShellItemImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    public static ImageSource? TryLoad(string path, int size)
    {
        try
        {
            var iid = ShellItemImageFactoryId;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            if (hr != 0 || factory is null)
            {
                return null;
            }

            try
            {
                var thumbnailSize = new NativeSize(size, size);
                factory.GetImage(thumbnailSize, ShellImageFlags.ThumbnailOnly, out var bitmapHandle);
                if (bitmapHandle == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(
                        bitmapHandle,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    DeleteObject(bitmapHandle);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindingContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? shellItem);

    [DllImport("gdi32.dll", PreserveSig = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize
    {
        public NativeSize(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public int Width { get; }

        public int Height { get; }
    }

    [Flags]
    private enum ShellImageFlags
    {
        BiggerSizeOk = 0x1,
        ThumbnailOnly = 0x8
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(NativeSize size, ShellImageFlags flags, out IntPtr bitmapHandle);
    }
}
