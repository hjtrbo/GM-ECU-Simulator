namespace Common.Protocol;

// Which diagnostic protocol stack a particular request arrived on. Real
// E38 / E67 silicon implements TWO physically separate dispatchers - one
// for GMLAN-enhanced-diag (the GMW3110 SID set) and one for OBD-II / UDS
// (the broader ISO 14229 + SAE J1979 SID set) - reached via different
// CAN IDs and serviced by different code paths. A given physical request
// can only land in one stack.
//
// The simulator tags every dispatched request with the stack the request
// CAN ID implies so handlers can NRC $11 ServiceNotSupported when called
// on the "wrong" stack (as real silicon does), and so future per-stack
// SID coverage can diverge cleanly. For now most handlers ignore the
// tag - the value of the enum is the plumbing it unlocks.
//
// See memory/project_dual_diag_stack_e38_e67.md for the static-analysis
// findings that motivated this split.
public enum DiagnosticStack
{
    /// <summary>GMLAN enhanced-diagnostics stack (the 9-SID GMW3110-2010 set
    /// reached via GMLAN diag CAN IDs - typically the $241/$641 or family-
    /// specific enhanced-diag pair). Default for legacy / unrecognised IDs
    /// so behaviour pre-dating this enum is preserved.</summary>
    Gmw3110 = 0,

    /// <summary>OBD-II / UDS stack (the 27-SID set on real E38/E67) reached
    /// via the standardised OBD CAN IDs $7DF (functional broadcast),
    /// $7E0..$7E7 (physical request) and $101 (GMLAN functional). Hosts the
    /// SAE J1979 mode set ($01..$0A) plus the UDS reads/writes the simulator
    /// already models ($22, $2C, $2D, $3B, $A9, $AA, $AE, plus the overlaps
    /// with GMW3110).</summary>
    Uds = 1,
}

public static class DiagnosticStackClassifier
{
    // CAN ID -> stack heuristic. Conservative: only the well-known OBD-II /
    // UDS IDs route to Uds; every other ID stays on Gmw3110 so existing
    // configs (which were built before this enum existed) continue to
    // dispatch through the GMW3110 persona unchanged.
    //
    // Range matches ISO 15765-4: $7DF for functional broadcast and
    // $7E0..$7E7 for physical request. $101 is GM's GMLAN functional
    // broadcast which routes through the OBD-II/UDS dispatcher on
    // surveyed E38/E67 silicon (see RE summary).
    public static DiagnosticStack StackForCanId(uint canId) => canId switch
    {
        0x7DF                       => DiagnosticStack.Uds,
        >= 0x7E0 and <= 0x7E7       => DiagnosticStack.Uds,
        0x101                       => DiagnosticStack.Uds,
        _                           => DiagnosticStack.Gmw3110,
    };
}
