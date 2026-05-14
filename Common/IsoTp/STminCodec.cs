namespace Common.IsoTp;

/// <summary>
/// Encodes and decodes the SeparationTime minimum (STmin) byte carried in
/// FlowControl N_PDU byte #3, per ISO 15765-2:2016 §9.6.5.4 / Table 20:
///
///   0x00..0x7F  => 0..127 ms
///   0x80..0xF0  => reserved (per §9.6.5.5, receiver shall use 127 ms = 0x7F)
///   0xF1..0xF9  => 100..900 microseconds (100 us steps)
///   0xFA..0xFF  => reserved (per §9.6.5.5, receiver shall use 127 ms)
///
/// Decode returns microseconds so the full sub-millisecond range round-trips
/// without loss. Reserved values are remapped to 127 ms (127 000 us).
/// </summary>
public static class STminCodec
{
    /// <summary>
    /// Decodes a raw STmin byte to microseconds. Reserved values are remapped
    /// to 127 ms per §9.6.5.5 - the spec mandates the receiver "use the longest
    /// STmin value specified by this part of ISO 15765 (7F = 127 ms)".
    /// </summary>
    public static int DecodeMicroseconds(byte raw)
    {
        if (raw <= 0x7F) return raw * 1000;
        if (raw >= 0xF1 && raw <= 0xF9) return (raw - 0xF0) * 100;
        return 127 * 1000;
    }

    /// <summary>
    /// Convenience wrapper - returns the integer millisecond ceiling of the
    /// decoded value. Sub-ms STmin (0xF1..0xF9) decodes to 1 ms here so that
    /// callers using only ms-resolution timers don't violate the spec by
    /// firing CFs back-to-back.
    /// </summary>
    public static int DecodeMillisecondsCeiling(byte raw)
    {
        int us = DecodeMicroseconds(raw);
        return (us + 999) / 1000;
    }

    /// <summary>
    /// Encodes a millisecond STmin into the raw byte. <paramref name="ms"/>
    /// must be 0..127 (the sub-ms range uses <see cref="EncodeMicroseconds"/>).
    /// </summary>
    public static byte EncodeMilliseconds(int ms)
    {
        if (ms < 0 || ms > 0x7F)
            throw new ArgumentOutOfRangeException(nameof(ms),
                "Use EncodeMicroseconds for sub-ms or > 127 ms STmin");
        return (byte)ms;
    }

    /// <summary>
    /// Encodes a sub-millisecond STmin in microseconds. <paramref name="us"/>
    /// must be one of {100, 200, 300, 400, 500, 600, 700, 800, 900} per
    /// Table 20 - the 100 us granularity is fixed by the standard.
    /// </summary>
    public static byte EncodeMicroseconds(int us)
    {
        if (us < 100 || us > 900 || us % 100 != 0)
            throw new ArgumentOutOfRangeException(nameof(us),
                "Sub-ms STmin must be a 100 us multiple in 100..900");
        return (byte)(0xF0 + us / 100);
    }

    /// <summary>True if <paramref name="raw"/> falls in a reserved range (§9.6.5.4).</summary>
    public static bool IsReserved(byte raw) => raw is >= 0x80 and <= 0xF0 or >= 0xFA;
}
