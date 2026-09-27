using System.Globalization;
using System.Windows.Data;

namespace PhotoShelf.Desktop;

public sealed class BooleanNotConverter : IValueConverter
{
    public static BooleanNotConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool boolValue && !boolValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool boolValue && !boolValue;
    }
}
