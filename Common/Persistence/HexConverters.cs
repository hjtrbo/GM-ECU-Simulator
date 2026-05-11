using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Persistence;

// JSON converters that emit "0xXXX" strings for CAN IDs / extended
// addresses and accept either hex strings ("0x241", "241h") or plain
// decimal numbers when reading. Matches Tester-Emu's hex-string style
// for readability.
public sealed class HexUShortConverter : JsonConverter<ushort>
{
    public override ushort Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetUInt16(),
            JsonTokenType.String => ParseHex(reader.GetString()),
            _ => throw new JsonException($"Expected number or hex string for ushort, got {reader.TokenType}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, ushort value, JsonSerializerOptions options)
        => writer.WriteStringValue($"0x{value:X3}");

    internal static ushort ParseHex(string? s)
    {
        if (s == null) throw new JsonException("null hex string");
        var t = s.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        else if (t.EndsWith('h') || t.EndsWith('H')) t = t[..^1];
        if (!ushort.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new JsonException($"Invalid hex ushort '{s}'");
        return v;
    }
}

// 32-bit hex converter for PID addresses, which can be full memory
// addresses (e.g. 0x002C0000). Same parse contract as the ushort variant
// — accepts "0x..." / "...h" / decimal — and emits leading-zero padding
// at the natural width: 4 hex digits for ≤0xFFFF, 8 hex digits otherwise.
public sealed class HexUIntConverter : JsonConverter<uint>
{
    public override uint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetUInt32(),
            JsonTokenType.String => ParseHex(reader.GetString()),
            _ => throw new JsonException($"Expected number or hex string for uint, got {reader.TokenType}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, uint value, JsonSerializerOptions options)
        => writer.WriteStringValue(value <= 0xFFFF ? $"0x{value:X4}" : $"0x{value:X8}");

    internal static uint ParseHex(string? s)
    {
        if (s == null) throw new JsonException("null hex string");
        var t = s.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        else if (t.EndsWith('h') || t.EndsWith('H')) t = t[..^1];
        if (!uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new JsonException($"Invalid hex uint '{s}'");
        return v;
    }
}

public sealed class HexByteConverter : JsonConverter<byte>
{
    public override byte Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetByte(),
            JsonTokenType.String => ParseHex(reader.GetString()),
            _ => throw new JsonException($"Expected number or hex string for byte, got {reader.TokenType}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, byte value, JsonSerializerOptions options)
        => writer.WriteStringValue($"0x{value:X2}");

    internal static byte ParseHex(string? s)
    {
        if (s == null) throw new JsonException("null hex string");
        var t = s.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        else if (t.EndsWith('h') || t.EndsWith('H')) t = t[..^1];
        if (!byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new JsonException($"Invalid hex byte '{s}'");
        return v;
    }
}
