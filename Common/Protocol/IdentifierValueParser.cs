using System.Globalization;
using System.Text;

namespace Common.Protocol;

/// <summary>
/// Two-way conversion between $1A identifier byte arrays and the user-typed
/// strings the editor surfaces. The grid in the WPF UI uses these helpers to
/// keep its dual-format Value column (ASCII text or whitespace-separated hex
/// bytes) consistent with the byte[] stored on <c>EcuNode.Identifiers</c>.
/// Pulled into Common so the test project can verify the parsing rules
/// without depending on the WPF assembly.
/// </summary>
public static class IdentifierValueParser
{
    /// <summary>
    /// True if every byte falls in the printable-ASCII range (0x20..0x7E)
    /// plus tab/CR/LF. Empty inputs are printable trivially. Used to decide
    /// whether a freshly-loaded identifier should show as text or hex in
    /// the editor.
    /// </summary>
    public static bool IsPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b == 0x09 || b == 0x0A || b == 0x0D) continue;
            if (b < 0x20 || b > 0x7E) return false;
        }
        return true;
    }

    /// <summary>
    /// Formats bytes as space-separated uppercase hex pairs ("48 65 6C 6C 6F").
    /// Empty input returns an empty string so a blank Value field is preserved.
    /// </summary>
    public static string ToHexString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return "";
        var sb = new StringBuilder(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses a whitespace/comma-separated list of hex byte tokens. Each token
    /// may carry an optional "0x" prefix. Returns null if any token is not a
    /// valid 1..2 digit hex byte; the caller can treat null as "keep the old
    /// bytes until the user types a valid string". Empty input returns an
    /// empty array.
    /// </summary>
    public static byte[]? TryParseHexBytes(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<byte>();
        var parts = s.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var tok = parts[i];
            if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) tok = tok[2..];
            if (!byte.TryParse(tok, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i]))
                return null;
        }
        return result;
    }

    /// <summary>
    /// Parses a single hex byte from user input. Accepts an optional "$" or
    /// "0x" prefix. False on malformed input; out parameter is left at 0.
    /// </summary>
    public static bool TryParseHexByte(string s, out byte v)
    {
        var trimmed = (s ?? "").Trim();
        if (trimmed.StartsWith("$")) trimmed = trimmed[1..];
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
        return byte.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }
}
