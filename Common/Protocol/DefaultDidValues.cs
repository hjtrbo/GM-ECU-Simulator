using System.Text;

namespace Common.Protocol;

/// <summary>
/// Placeholder $1A identifier values used by the editor's "Auto-populate"
/// command. Each entry returns a byte array shaped like the real wire value
/// (right length, right encoding) but with obviously synthetic content - the
/// goal is a one-click "make this ECU plausible enough for a tester to talk
/// to" without inventing forged GM part numbers / VINs.
///
/// Auto-populate is precedence-aware in the caller: only DIDs that are
/// currently unset get filled, so re-running won't clobber a value the user
/// hand-typed or that "Load Info From Bin..." pulled out of a flash image.
///
/// Format notes for the values below:
///   - VINs and part-number strings are 17 / 8 ASCII chars to match the
///     wire-format length real testers expect.
///   - Numeric "BE uint32" DIDs ($C0..$CE) emit 4 raw bytes; the high byte
///     is set so testers don't accidentally pattern-match a zero field as
///     "blank".
///   - Date fields are encoded as ASCII YYYYMMDD - matches what the bin
///     loader's ProgrammingDate extractor produces and what the SegmentReader
///     comments document.
///   - Counters and single-byte DIDs default to 0x00 (the "no fault" / "not
///     yet incremented" value for the manufacturers-enable counter etc.).
/// </summary>
public static class DefaultDidValues
{
    /// <summary>
    /// Returns a placeholder byte array for the given DID, or null if no
    /// default is defined (the caller leaves that DID unconfigured).
    /// </summary>
    public static byte[]? Get(byte did) => did switch
    {
        0x28 => Ascii("SIM000"),                            // Partial VIN (last 6)
        0x90 => Ascii("SIMVIN00000000000"),                  // VIN (17 chars)
        0x92 => Ascii("SIM-SUP-ID"),                         // System Supplier ID
        0x95 => Ascii("SIMSWV01"),                           // Supplier SW Version Number
        0x97 => Ascii("SimEngine"),                          // System Name / Engine Type
        0x98 => Ascii("SIM-REPSHOP"),                        // Repair Shop Code / SN
        0x99 => Ascii(DateTime.UtcNow.ToString("yyyyMMdd")), // Programming Date
        0x9A => new byte[] { 0x01, 0x00 },                   // Diagnostic Data Identifier (opaque blob)
        0x9B => new byte[] { 0x00, 0x00, 0x00, 0x00 },       // ECU Configuration / Coding
        0x9F => Ascii("00000000"),                           // History: RSCOSN
        0xA0 => new byte[] { 0x00 },                         // Manufacturers Enable Counter
        0xB0 => new byte[] { 0x11 },                         // Diagnostic Address (matches GMLAN $11 default)
        0xB4 => Ascii("SIM-TRACE-000000"),                   // Mfg Traceability Chars
        0xB5 => Ascii("SIMC"),                               // Broadcast Code (4 chars)
        0xC0 => BeUInt32(0x12345600),                        // Operating Software ID
        0xC1 => BeUInt32(0x12345601),                        // End Model Part Number
        0xC2 => BeUInt32(0x12345602),                        // Base Model Part Number
        0xC3 => BeUInt32(0x12345603),                        // Operating SW (alt)
        0xC4 => BeUInt32(0x12345604),                        // Calibration ID 1
        0xC5 => BeUInt32(0x12345605),                        // Calibration ID 2
        0xC6 => BeUInt32(0x12345606),                        // Calibration ID 3
        0xC7 => BeUInt32(0x12345607),                        // Calibration ID 4
        0xC8 => BeUInt32(0x12345608),                        // Calibration ID 5
        0xC9 => BeUInt32(0x12345609),                        // Calibration ID 6
        0xCA => BeUInt32(0x1234560A),                        // Calibration ID 7
        0xCB => BeUInt32(0x1234560B),                        // End Model Number
        0xCC => BeUInt32(0x1234560C),                        // Base Model Number
        0xCD => Ascii("SIM-HW-PN"),                          // ECU Hardware P/N
        0xCE => Ascii("HW-v1.0"),                            // ECU Hardware Version
        // $D0..$DA - 2-char ASCII Alpha Code per GMW3110 §8.3.2. "AA" is
        // the un-revised factory placeholder ("AB", "AC", ... on re-released
        // cals). Matches what the bin extractor pulls out of stock images.
        0xD0 => Ascii("AA"),                                 // Boot SW Alpha Code
        0xD1 => Ascii("AA"),                                 // SW Alpha Code 1
        0xD2 => Ascii("AA"),
        0xD3 => Ascii("AA"),
        0xD4 => Ascii("AA"),
        0xD5 => Ascii("AA"),
        0xD6 => Ascii("AA"),
        0xD7 => Ascii("AA"),
        0xD8 => Ascii("AA"),
        0xD9 => Ascii("AA"),
        0xDA => Ascii("AA"),                                 // SW Alpha Code 10
        0xDD => new byte[] { 0x01, 0x00, 0x00, 0x00 },       // Software Module Identifier
        0xF1 => new byte[] { 0xF1, 0x00 },                   // ECU Specific Data 1
        0xF2 => new byte[] { 0xF2, 0x00 },
        0xF3 => new byte[] { 0xF3, 0x00 },
        0xF4 => new byte[] { 0xF4, 0x00 },
        _ => null,
    };

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] BeUInt32(uint v) => new byte[]
    {
        (byte)(v >> 24),
        (byte)(v >> 16),
        (byte)(v >> 8),
        (byte)v,
    };
}
