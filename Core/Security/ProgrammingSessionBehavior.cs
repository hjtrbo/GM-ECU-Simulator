namespace Core.Security;

// Declares how an ISeedKeyAlgorithm behaves once the ECU has entered a
// programming session (via $10 $02 InitiateDiagnosticOperation, or via the
// full GMW3110 $28 -> $A5 $01/$02 -> $A5 $03 chain). The Gmw3110_2010_Generic
// module consults the algorithm's policy on each $27 step.
//
// Why this is per-algorithm rather than per-module or per-ECU: real GM
// hardware varies by family. The T43 TCM (6L80/6L90) has a permissive
// boot-block $27 stub - real testers like 6Speed.T43 rely on it returning
// seed = 00 00 and accepting any key (including the hardcoded 00 00 in
// 6Speed's sendKey path). The E38/E67 ECM bootloader is just as secure as
// the OS and continues to enforce the full GMLAN 0x92 algorithm in
// programming session. Modelling this with a single global flag would
// either break the T43-style flow or weaken E38-style security.
public enum ProgrammingSessionBehavior
{
    /// <summary>
    /// Default. The same seed/key algorithm runs whether or not the ECU
    /// is in programming session. Matches E38, E67, and most modern GM
    /// ECMs whose bootloader enforces the same security as the OS.
    /// </summary>
    UnchangedAlgorithm = 0,

    /// <summary>
    /// Programming session short-circuits $27: requestSeed returns
    /// seed = 00 00, sendKey accepts any key. Matches the T43 TCM
    /// boot-block stub at file offset 0x2BBFC of a real 24264923 image -
    /// the design 6Speed.T43 was written against.
    /// </summary>
    BypassAll,
}
