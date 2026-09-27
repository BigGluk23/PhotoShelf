using System.Windows;

namespace PhotoShelf.Desktop;

// A normal property path avoids the XAML attached-property TypeConverter involved in the upstream startup fix.
public sealed class PreviewImage : System.Windows.Controls.Image
{
    public static readonly DependencyProperty StatusProperty = AsyncMediaImage.StatusProperty.AddOwner(typeof(PreviewImage));
    public string Status => (string)GetValue(StatusProperty);
}
