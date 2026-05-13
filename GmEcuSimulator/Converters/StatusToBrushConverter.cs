using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GmEcuSimulator.Converters;

// Maps a J2534-status string ("✓ Registered (...)", "Not registered",
// "(checking…)", "Status check failed: …") onto a semantic brush so the
// status pill can light up green / amber / red without any extra plumbing
// in the ViewModel. Resolves brushes from App.Current.Resources so it
// honours the active theme.
[ValueConversion(typeof(string), typeof(Brush))]
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? string.Empty;
        var key =
            s.StartsWith("✓") || s.StartsWith("OK", StringComparison.OrdinalIgnoreCase) ? "Status.SuccessBrush"
          : s.StartsWith("Not", StringComparison.OrdinalIgnoreCase)                          ? "Status.WarningBrush"
          : s.Contains("failed", StringComparison.OrdinalIgnoreCase)                         ? "Status.ErrorBrush"
          :                                                                                    "Text.TertiaryBrush";
        return (Application.Current?.Resources[key] as Brush) ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
