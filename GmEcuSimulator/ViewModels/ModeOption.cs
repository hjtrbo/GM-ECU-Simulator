using Common;

namespace GmEcuSimulator.ViewModels;

// One entry in the mode-selector ComboBox. Pairs the top-level AppMode (what is
// simulated) with the ConnectionType (which transport carries it), so the
// dropdown can offer "ECU Simulator - J2534" and "ECU Simulator - TCP" as
// distinct picks while the underlying mode stays the same. Record gives value
// equality on (Mode, Connection) so the ComboBox highlights the current pick;
// Label is derived and excluded from equality.
public sealed record ModeOption(AppMode Mode, ConnectionType Connection)
{
    // DPS programming is J2534-only - a gauge over TCP has no meaning for a
    // programming session - so its entry omits the connection suffix.
    public string Label => Mode == AppMode.DpsSimulator
        ? Mode.DisplayName()
        : $"{Mode.DisplayName()} - {Connection.DisplayName()}";
}
