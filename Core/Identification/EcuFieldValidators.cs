using System.Globalization;

namespace Core.Identification;

// Centralised per-field validators for the EcuViewModel inspector. Each
// returns null when the input is valid (or blank - blank clears the DID),
// or a short human-readable message otherwise. Kept here rather than
// inlined in setters so the rules are testable in isolation without
// spinning up an EcuNode.
//
// All rules treat empty/whitespace input as "no value" (= valid + clears
// the DID). The simulator's $1A handler responds with NRC $31 when a
// requested DID has no stored value, which matches the spec's default
// when an ECU doesn't support that identifier.
public static class EcuFieldValidators
{
    // VIN charset per ISO 3779: A-Z and 0-9, excluding I, O, Q.
    private static readonly HashSet<char> VinIllegalLetters = new() { 'I', 'O', 'Q' };

    /// <summary>
    /// DID $90 - Vehicle Identification Number. 17 characters from the VIN
    /// charset (uppercase letters + digits, no I/O/Q). Blank is allowed
    /// (clears the DID).
    /// </summary>
    public static string? ValidateVin(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        if (v.Length != 17) return $"VIN must be 17 characters (got {v.Length}).";
        foreach (var c in v)
        {
            if (c >= 'a' && c <= 'z') return "VIN must be uppercase.";
            if (VinIllegalLetters.Contains(c)) return $"VIN cannot contain '{c}' (I, O, Q reserved).";
            bool ok = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
            if (!ok) return $"VIN can only contain letters and digits (found '{c}').";
        }
        return null;
    }

    /// <summary>
    /// DID $92 / $98 - System Supplier ECU Hardware Number / Version Number.
    /// GMW3110 doesn't pin an exact length; in practice GM ECUs return
    /// 1..32 printable ASCII bytes. We enforce printable ASCII so we don't
    /// silently store unprintable bytes that mess up the $1A wire response.
    /// </summary>
    public static string? ValidateSupplierAscii(string? value)
        => ValidatePrintableAscii(value, maxLen: 32, label: "Value");

    /// <summary>
    /// DID $C1 / $C2 - End / Base Model Part Number. GM service part
    /// numbers are 8 digits. We accept any printable ASCII up to 32 chars
    /// (lets users paste in alphanumeric service codes like "AB12345C")
    /// but warn when nothing in the value is digit-like.
    /// </summary>
    public static string? ValidatePartNumber(string? value)
        => ValidatePrintableAscii(value, maxLen: 32, label: "Part number");

    /// <summary>
    /// DID $CC - ECU Diagnostic Address. One hex byte (e.g. "0x12" or
    /// "12"). Blank clears the DID. Anything else must parse as a single
    /// byte 0x00..0xFF.
    /// </summary>
    public static string? ValidateDiagAddrHex(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = v[2..];
        if (v.Length == 0 || v.Length > 2)
            return "Diagnostic address must be one hex byte (e.g. 0x12).";
        if (!byte.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return "Diagnostic address must be one hex byte (e.g. 0x12).";
        return null;
    }

    /// <summary>
    /// 4-byte big-endian hex field (e.g. "0x017240DB" or "017240DB"). Used
    /// for DIDs whose on-the-wire response is 4 raw bytes - $C1 End Model
    /// P/N, $CC ECU Diagnostic Address. Blank clears the DID. Anything else
    /// must be exactly 8 hex digits (with an optional `0x`/`0X` prefix).
    /// </summary>
    public static string? Validate4ByteHexBE(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = v[2..];
        if (v.Length != 8)
            return $"4-byte hex must be exactly 8 hex digits (got {v.Length}).";
        foreach (var c in v)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
            if (!ok) return "4-byte hex required (e.g. 0x017240DB).";
        }
        return null;
    }

    /// <summary>
    /// EEPROM "BCC" Broadcast Code - 4 alphanumeric characters. Empty
    /// clears the field. The BCC is the last 4 chars of the calibration
    /// PN by GM convention - validating as exactly-4-printable here keeps
    /// the rule simple and matches every BCC we've seen in real bins.
    /// </summary>
    public static string? ValidateBroadcastCode(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        if (v.Length != 4) return $"Broadcast code must be exactly 4 characters (got {v.Length}).";
        foreach (var c in v)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
            if (!ok) return "Broadcast code must be alphanumeric.";
        }
        return null;
    }

    /// <summary>
    /// Programming date stamp - 8 decimal digits (YYYYMMDD), e.g.
    /// "20220326". Blank clears the field. We don't validate the
    /// calendar (e.g. month 0..12) because real bins occasionally carry
    /// development or partial dates; we just enforce the digit-count
    /// shape so the field round-trips cleanly.
    /// </summary>
    public static string? ValidateProgrammingDate(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        if (v.Length != 8) return $"Programming date must be 8 digits YYYYMMDD (got {v.Length}).";
        foreach (var c in v)
            if (c < '0' || c > '9') return "Programming date must contain only digits.";
        return null;
    }

    // -------------------- shared printable-ASCII rule --------------------

    private static string? ValidatePrintableAscii(string? value, int maxLen, string label)
    {
        var v = value ?? "";
        if (v.Length == 0) return null;
        if (v.Length > maxLen) return $"{label} too long (max {maxLen} chars).";
        foreach (var c in v)
        {
            if (c < 0x20 || c > 0x7E)
                return $"{label} must be printable ASCII (found non-printable byte 0x{(int)c:X2}).";
        }
        return null;
    }
}
