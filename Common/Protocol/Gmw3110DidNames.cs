namespace Common.Protocol;

/// <summary>
/// Well-known $1A ReadDataByIdentifier DID labels per GMW3110 §8.3.2 Table 25
/// and Appendix A (the SPS / DPS Get-Controller-Info dialog field set). Used by
/// the editor's Identifiers grid to label rows so the user can see which DID
/// is which without cross-referencing the spec. Unknown DIDs return null and
/// the grid shows just the hex byte.
///
/// <see cref="KnownDids"/> is the full enumerable set in the order the
/// editor's Identifiers grid renders them. Keep it sorted by DID byte so
/// the table is predictable; add new entries to <see cref="NameOf"/> too.
/// </summary>
public static class Gmw3110DidNames
{
    /// <summary>
    /// Every DID the editor pre-populates in the Identifiers grid. Matches the
    /// set DPS Get-Controller-Info reads, plus a few neighbouring identifiers
    /// (DTC counts, hardware part numbers) that other GM testers ask for.
    /// </summary>
    public static readonly byte[] KnownDids = new byte[]
    {
        0x28,
        0x90,
        0x92, 0x95, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9F,
        0xA0, 0xB0, 0xB4, 0xB5,
        0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE,
        0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xDD,
        0xF1, 0xF2, 0xF3, 0xF4,
    };

    public static string? NameOf(byte did) => did switch
    {
        0x28 => "Partial VIN",
        0x90 => "VIN",
        0x92 => "System Supplier ID",
        0x95 => "Supplier SW Version Number",
        0x97 => "System Name / Engine Type",
        0x98 => "Repair Shop Code / SN",
        0x99 => "Programming Date",
        0x9A => "Diagnostic Data Identifier",
        0x9B => "ECU Configuration / Coding",
        0x9F => "History: RSCOSN",
        0xA0 => "Manufacturers Enable Counter",
        0xB0 => "Diagnostic Address",
        0xB4 => "Mfg Traceability Chars",
        0xB5 => "Broadcast Code",
        0xC0 => "Operating Software ID",
        0xC1 => "End Model Part Number",
        0xC2 => "Base Model Part Number",
        0xC3 => "Operating SW P/N (alt)",
        0xC4 => "Calibration ID 1",
        0xC5 => "Calibration ID 2",
        0xC6 => "Calibration ID 3",
        0xC7 => "Calibration ID 4",
        0xC8 => "Calibration ID 5",
        0xC9 => "Calibration ID 6",
        0xCA => "Calibration ID 7",
        0xCB => "End Model Number",
        0xCC => "Base Model Number",
        0xCD => "ECU Hardware P/N",
        0xCE => "ECU Hardware Version",
        // $D0 = boot SW alpha code; $D1..$DA = 2-char design-level suffix
        // (Alpha Code) for the SWMIs at $C1..$CA. GMW3110-2010 §8.3.2.
        0xD0 => "Boot SW Alpha Code",
        0xD1 => "SW Alpha Code 1",
        0xD2 => "SW Alpha Code 2",
        0xD3 => "SW Alpha Code 3",
        0xD4 => "SW Alpha Code 4",
        0xD5 => "SW Alpha Code 5",
        0xD6 => "SW Alpha Code 6",
        0xD7 => "SW Alpha Code 7",
        0xD8 => "SW Alpha Code 8",
        0xD9 => "SW Alpha Code 9",
        0xDA => "SW Alpha Code 10",
        0xDD => "Software Module Identifier",
        0xF1 => "ECU Specific Data 1",
        0xF2 => "ECU Specific Data 2",
        0xF3 => "ECU Specific Data 3",
        0xF4 => "ECU Specific Data 4",
        _ => null,
    };
}
