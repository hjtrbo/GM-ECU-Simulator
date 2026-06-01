namespace Common;

// How a host reaches the simulator. Orthogonal to AppMode (what is simulated):
// exactly one transport is live at a time, selected as a sub-variant inside the
// mode dropdown ("ECU Simulator - J2534" / "ECU Simulator - TCP").
//
// J2534     - the native PassThruShim DLL connects over the named pipe
//             \\.\pipe\GmEcuSim.PassThru (the original, registry-discovered path).
// RawCanTcp - a localhost TCP listener carrying raw CAN frames, so a separately
//             developed gauge simulator can join the bus as if it were a node on
//             a shared wire. ISO-TP runs on both ends; the wire carries single
//             CAN frames only, never reassembled USDT messages.
public enum ConnectionType
{
    J2534 = 0,
    RawCanTcp = 1,
}

public static class ConnectionTypeExtensions
{
    // Short label for the combined mode-dropdown entry. Kept terse because it
    // is suffixed onto the AppMode display name ("ECU Simulator - J2534").
    public static string DisplayName(this ConnectionType c) => c switch
    {
        ConnectionType.J2534 => "J2534",
        ConnectionType.RawCanTcp => "TCP",
        _ => c.ToString(),
    };
}
