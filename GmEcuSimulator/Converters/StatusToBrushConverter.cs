using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GmEcuSimulator.Converters;

// Maps a status string onto a semantic brush so a status pill can light up
// green / amber / red without any extra plumbing in the ViewModel. Used by
// both the J2534 registration pill ("Registered ...", "Not registered",
// "Status check failed: ...") and the ECU security pill ("Locked",
// "Unlocked (level N)", "Locked out - ..."). Resolves brushes from
// App.Current.Resources so it honours the active theme.
[ValueConversion(typeof(string), typeof(Brush))]
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? string.Empty;
        // Order matters: "Locked out" must beat "Locked", and any "Not ..."
        // negative state must beat the positive prefix it negates. Check the
        // more-specific patterns first. Both the J2534 pill ("Shim Registered"
        // / "Shim Not Registered") and the ECU security pill ("Locked",
        // "Unlocked", "Locked out - ...") feed through here, so the contains-
        // based checks below have to handle either spelling.
        var key =
            s.Contains("Locked out", StringComparison.OrdinalIgnoreCase) ? "Status.ErrorBrush"
          : s.Contains("Unlocked",   StringComparison.OrdinalIgnoreCase) ? "Status.SuccessBrush"
          : s.Contains("Fault",      StringComparison.OrdinalIgnoreCase) ? "Status.WarningBrush"
          : s.Contains("Not",        StringComparison.OrdinalIgnoreCase) ? "Status.WarningBrush"
          : s.Contains("Locked",     StringComparison.OrdinalIgnoreCase) ? "Status.WarningBrush"
          : s.Contains("Registered", StringComparison.OrdinalIgnoreCase) ? "Status.SuccessBrush"
          // Raw-CAN TCP gauge link: connected/listening are healthy, stopped is idle.
          : s.Contains("connected",  StringComparison.OrdinalIgnoreCase) ? "Status.SuccessBrush"
          : s.Contains("listening",  StringComparison.OrdinalIgnoreCase) ? "Status.SuccessBrush"
          : s.Contains("stopped",    StringComparison.OrdinalIgnoreCase) ? "Status.WarningBrush"
          : s.Contains("OK",         StringComparison.OrdinalIgnoreCase) ? "Status.SuccessBrush"
          : s.Contains("failed",     StringComparison.OrdinalIgnoreCase) ? "Status.ErrorBrush"
          :                                                                "Text.TertiaryBrush";
        return (Application.Current?.Resources[key] as Brush) ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
