using System.IO;
using Drawing = System.Drawing;

namespace PhotoShelf.Desktop;

public static class AppResources
{
    public const string IconUri = "pack://application:,,,/PhotoShelf;component/Assets/PhotoShelf.ico";
    public const string GiraffeUri = "pack://application:,,,/PhotoShelf;component/Assets/giraffe-icon.png";

    public static Stream OpenIconStream() => Open(IconUri);

    public static Stream Open(string uri) => System.Windows.Application.GetResourceStream(new Uri(uri, UriKind.Absolute))?.Stream
        ?? throw new FileNotFoundException($"Встроенный ресурс PhotoShelf не найден: {uri}");

    public static Drawing.Icon CreateTrayIcon()
    {
        using var stream = OpenIconStream();
        using var icon = new Drawing.Icon(stream);
        return (Drawing.Icon)icon.Clone();
    }
}
