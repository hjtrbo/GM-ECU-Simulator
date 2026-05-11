namespace Common.Protocol;

// Engineering-unit ↔ wire-byte conversion. The wire format on GMLAN is
// big-endian per GMW3110; this is the inverse of the DataLogger's
// Channel.cs::ProcessValue (Gm Data Logger_v5_Wpf_WIP/Core/DataObjects/Channel.cs:367).
//
// Engineering value -> raw integer:  raw = round((value - offset) / scalar)
// Raw is then clamped to the type's range and serialised big-endian into the dest span.
public static class ValueCodec
{
    public static void Encode(
        double engValue, double scalar, double offset,
        PidDataType type, int sizeBytes, Span<byte> dest)
    {
        if (dest.Length < sizeBytes)
            throw new ArgumentException($"dest too small: {dest.Length} < {sizeBytes}");

        // Inverse linear scaling. Guard against scalar=0 (treat as identity).
        double raw = scalar != 0 ? (engValue - offset) / scalar : engValue - offset;

        switch (type)
        {
            case PidDataType.Bool:
                dest[0] = raw >= 0.5 ? (byte)1 : (byte)0;
                break;

            case PidDataType.Unsigned:
            case PidDataType.Hex:
            {
                ulong max = sizeBytes switch { 1 => 0xFFu, 2 => 0xFFFFu, 4 => 0xFFFFFFFFu, _ => throw new ArgumentOutOfRangeException(nameof(sizeBytes)) };
                ulong v = raw <= 0 ? 0UL : raw >= max ? max : (ulong)Math.Round(raw);
                WriteBigEndian(dest, sizeBytes, v);
                break;
            }

            case PidDataType.Signed:
            {
                long min = sizeBytes switch { 1 => sbyte.MinValue, 2 => short.MinValue, 4 => int.MinValue, _ => throw new ArgumentOutOfRangeException(nameof(sizeBytes)) };
                long max = sizeBytes switch { 1 => sbyte.MaxValue, 2 => short.MaxValue, 4 => int.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(sizeBytes)) };
                long v = raw <= min ? min : raw >= max ? max : (long)Math.Round(raw);
                ulong unsigned = sizeBytes switch
                {
                    1 => (ulong)(byte)(sbyte)v,
                    2 => (ulong)(ushort)(short)v,
                    4 => (ulong)(uint)(int)v,
                    _ => throw new ArgumentOutOfRangeException(nameof(sizeBytes)),
                };
                WriteBigEndian(dest, sizeBytes, unsigned);
                break;
            }

            case PidDataType.Ascii:
                // ASCII PIDs return a fixed-length character buffer. The "engValue"
                // semantics don't really apply; for the simulator we emit '?' fill so
                // it's obvious in a log that nothing meaningful was set up.
                dest.Slice(0, sizeBytes).Fill((byte)'?');
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static void WriteBigEndian(Span<byte> dest, int sizeBytes, ulong v)
    {
        for (int i = 0; i < sizeBytes; i++)
            dest[i] = (byte)((v >> (8 * (sizeBytes - 1 - i))) & 0xFF);
    }
}
