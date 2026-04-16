using System.Globalization;

namespace Vitals.Maui.Converters;

public class BoolToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is string options)
        {
            var parts = options.Split('|');
            if (parts.Length == 2)
                return value is true ? parts[0] : parts[1];
        }
        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}