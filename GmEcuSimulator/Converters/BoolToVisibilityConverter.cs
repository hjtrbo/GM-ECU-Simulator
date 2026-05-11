using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GmEcuSimulator.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool show = parameter is string p && p.Equals("NotNull", StringComparison.OrdinalIgnoreCase)
            ? value != null
            : value is bool b && b;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}
