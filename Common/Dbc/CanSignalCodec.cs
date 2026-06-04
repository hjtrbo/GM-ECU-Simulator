namespace Common.Dbc;

// Bit-level pack/unpack of a DBC signal into a CAN payload. This is the piece ValueCodec cannot do:
// ValueCodec writes whole big-endian byte spans, whereas a DBC signal can be any 1..64-bit field at
// any start bit, in Motorola (big-endian sawtooth) or Intel (little-endian) order, signed or not,
// with its own scale/offset.
//
// Bit numbering (shared by both orders, this is what the SG_ start-bit field uses):
//   payload bit position p -> byte p/8, bit p%8 within that byte, where bit 0 is the LSB.
//
// Intel (@1): the signal's LSB is at `StartBit`; successive value bits go to increasing positions.
// Motorola (@0): the signal's MSB is at `StartBit`; successive (less significant) value bits go to
//   decreasing positions, but when a bit hits the bottom of a byte (p%8 == 0) the walk jumps to the
//   TOP of the next byte (p + 15). That sawtooth is why a 16-bit Motorola signal at start bit 7 lands
//   big-endian across bytes 0 and 1 (e.g. 800 rpm * 4 = 3200 = 0x0C80 -> byte0=0x0C, byte1=0x80).
public static class CanSignalCodec
{
    // Encode a physical (engineering-unit) value into the signal's field of `payload`.
    // raw = round((physical - Offset) / Scale), clamped to the field's width + signedness, then the
    // low `Length` bits (two's complement for signed) are written bit by bit. Bits that would fall
    // outside `payload` are skipped defensively (a well-formed DBC + matching DLC never hits this).
    public static void Pack(Span<byte> payload, DbcSignal sig, double physical)
        => Pack(payload, sig.StartBit, sig.Length, sig.ByteOrder, sig.Signed, sig.Scale, sig.Offset, physical);

    // Primitive overload so the runtime broadcast model can pack without allocating a DbcSignal per
    // tick (its layout fields are mutable in the editor).
    public static void Pack(Span<byte> payload, int startBit, int length, DbcByteOrder order,
                            bool signed, double scale, double offset, double physical)
    {
        ulong bits = ToRawBits(length, signed, scale, offset, physical);

        if (order == DbcByteOrder.Intel)
        {
            for (int j = 0; j < length; j++)
                WriteBit(payload, startBit + j, (bits >> j) & 1UL);
        }
        else // Motorola: MSB first, sawtooth walk downward
        {
            int p = startBit;
            for (int k = 0; k < length; k++)
            {
                ulong bit = (bits >> (length - 1 - k)) & 1UL;
                WriteBit(payload, p, bit);
                p = (p % 8 == 0) ? p + 15 : p - 1;
            }
        }
    }

    // Inverse of Pack: read the field's raw bits back, sign-extend if needed, and apply scale/offset.
    // Used by the round-trip unit tests and the editor's live readout.
    public static double Unpack(ReadOnlySpan<byte> payload, DbcSignal sig)
    {
        ulong bits = 0;
        if (sig.ByteOrder == DbcByteOrder.Intel)
        {
            for (int j = 0; j < sig.Length; j++)
                bits |= ReadBit(payload, sig.StartBit + j) << j;
        }
        else // Motorola: MSB first
        {
            int p = sig.StartBit;
            for (int k = 0; k < sig.Length; k++)
            {
                bits = (bits << 1) | ReadBit(payload, p);
                p = (p % 8 == 0) ? p + 15 : p - 1;
            }
        }

        long raw;
        if (sig.Signed && sig.Length < 64 && (bits & (1UL << (sig.Length - 1))) != 0)
            raw = (long)(bits | ~Mask(sig.Length));   // sign-extend
        else
            raw = (long)bits;

        return raw * sig.Scale + sig.Offset;
    }

    // physical -> the unsigned bit pattern (two's complement for signed) to drop into the field,
    // clamped to the field's representable range so an out-of-range live value never corrupts
    // neighbouring signals.
    private static ulong ToRawBits(int length, bool signed, double scale, double offset, double physical)
    {
        double r = scale != 0 ? (physical - offset) / scale : physical - offset;
        double rounded = Math.Round(r, MidpointRounding.AwayFromZero);
        ulong mask = Mask(length);

        if (signed)
        {
            long min = length >= 64 ? long.MinValue : -(1L << (length - 1));
            long max = length >= 64 ? long.MaxValue : (1L << (length - 1)) - 1;
            long raw = rounded <= min ? min : rounded >= max ? max : (long)rounded;
            return (ulong)raw & mask;
        }
        else
        {
            ulong umax = mask;
            ulong raw = rounded <= 0 ? 0UL : rounded >= umax ? umax : (ulong)rounded;
            return raw & mask;
        }
    }

    // The absolute payload bit positions a field occupies, in MSB->LSB (Motorola) or LSB->MSB
    // (Intel) order. Used by the importer to detect overlapping fields when merging signals from
    // another DBC into an existing message.
    public static int[] BitPositions(int startBit, int length, DbcByteOrder order)
    {
        var arr = new int[length];
        if (order == DbcByteOrder.Intel)
        {
            for (int j = 0; j < length; j++) arr[j] = startBit + j;
        }
        else
        {
            int p = startBit;
            for (int k = 0; k < length; k++)
            {
                arr[k] = p;
                p = (p % 8 == 0) ? p + 15 : p - 1;
            }
        }
        return arr;
    }

    private static ulong Mask(int length) => length >= 64 ? ulong.MaxValue : (1UL << length) - 1UL;

    private static void WriteBit(Span<byte> payload, int pos, ulong bit)
    {
        int byteIndex = pos >> 3;
        if ((uint)byteIndex >= (uint)payload.Length) return;   // defensive: signal exceeds DLC
        int bitInByte = pos & 7;
        if (bit != 0) payload[byteIndex] |= (byte)(1 << bitInByte);
        else          payload[byteIndex] &= (byte)~(1 << bitInByte);
    }

    private static ulong ReadBit(ReadOnlySpan<byte> payload, int pos)
    {
        int byteIndex = pos >> 3;
        if ((uint)byteIndex >= (uint)payload.Length) return 0;
        int bitInByte = pos & 7;
        return (ulong)((payload[byteIndex] >> bitInByte) & 1);
    }
}
