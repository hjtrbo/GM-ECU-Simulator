namespace Common.IsoTp;

/// <summary>
/// ISO 15765-2:2016 Protocol Control Information (N_PCI). Encodes and decodes
/// the four N_PDU types: SingleFrame (SF), FirstFrame (FF), ConsecutiveFrame
/// (CF) and FlowControl (FC). See §9.6 (pp. 25-32).
///
/// All encode/decode operations work on the "frame data field" - the bytes
/// after any addressing prefix has been stripped. For normal addressing the
/// frame data field is the entire CAN frame payload; for extended/mixed
/// addressing the caller must skip the N_TA or N_AE byte first (§10.3).
/// </summary>
public static class NPci
{
    /// <summary>Maximum SF_DL when CAN_DL == 8 with normal addressing (§9.6.2.1, Table 10).</summary>
    public const int MaxClassicalSfDlNormal = 7;
    /// <summary>Maximum SF_DL when CAN_DL == 8 with extended/mixed addressing (§9.6.2.1, Table 10).</summary>
    public const int MaxClassicalSfDlExtMixed = 6;
    /// <summary>Maximum FF_DL using the 12-bit length field (§9.6.3.1, Table 15).</summary>
    public const int MaxShortFfDl = 4095;

    // =========================================================================
    // SingleFrame
    // =========================================================================

    /// <summary>
    /// Writes an SF N_PCI + data into <paramref name="dest"/> using the
    /// classical (CAN_DL ≤ 8) encoding from §9.6.2.1, Table 9 row 1:
    /// Byte #1 = 0x0_ where the low nibble is SF_DL.
    /// </summary>
    /// <returns>Number of bytes written (1 + data.Length).</returns>
    public static int EncodeSingleFrameClassical(Span<byte> dest, ReadOnlySpan<byte> data)
    {
        if (data.Length < 1 || data.Length > MaxClassicalSfDlNormal)
            throw new ArgumentOutOfRangeException(nameof(data),
                $"Classical SF payload must be 1..{MaxClassicalSfDlNormal} bytes");
        if (dest.Length < 1 + data.Length)
            throw new ArgumentException("Destination too small", nameof(dest));
        dest[0] = (byte)(data.Length & 0x0F);
        data.CopyTo(dest[1..]);
        return 1 + data.Length;
    }

    /// <summary>
    /// Writes an SF N_PCI + data using the CAN-FD escape encoding from
    /// §9.6.2.1, Table 9 row 2 / Table 11: Byte #1 = 0x00, Byte #2 = SF_DL.
    /// Required when SF_DL is encoded inside a CAN_DL > 8 frame.
    /// </summary>
    /// <returns>Number of bytes written (2 + data.Length).</returns>
    public static int EncodeSingleFrameEscape(Span<byte> dest, ReadOnlySpan<byte> data)
    {
        if (data.Length < 1 || data.Length > 0xFF)
            throw new ArgumentOutOfRangeException(nameof(data));
        if (dest.Length < 2 + data.Length)
            throw new ArgumentException("Destination too small", nameof(dest));
        dest[0] = 0x00;
        dest[1] = (byte)data.Length;
        data.CopyTo(dest[2..]);
        return 2 + data.Length;
    }

    // =========================================================================
    // FirstFrame
    // =========================================================================

    /// <summary>
    /// Writes an FF N_PCI header (no payload) using the 12-bit FF_DL form
    /// (§9.6.3.1, Table 9 row 3): Byte #1 hi nibble = 0x1, low nibble +
    /// Byte #2 = FF_DL.
    /// </summary>
    /// <returns>Always 2 (PCI byte count).</returns>
    public static int EncodeFirstFrameShort(Span<byte> dest, ushort ffDl)
    {
        if (ffDl > MaxShortFfDl)
            throw new ArgumentOutOfRangeException(nameof(ffDl),
                $"Use EncodeFirstFrameEscape for FF_DL > {MaxShortFfDl}");
        if (dest.Length < 2)
            throw new ArgumentException("Destination too small", nameof(dest));
        dest[0] = (byte)(0x10 | ((ffDl >> 8) & 0x0F));
        dest[1] = (byte)(ffDl & 0xFF);
        return 2;
    }

    /// <summary>
    /// Writes an FF N_PCI header (no payload) using the 32-bit FF_DL escape
    /// (§9.6.3.1, Table 9 row 4): Byte #1 = 0x10, Byte #2 = 0x00, Bytes #3..#6
    /// carry FF_DL big-endian (MSB at #3, LSB at #6).
    /// </summary>
    /// <returns>Always 6 (PCI byte count).</returns>
    public static int EncodeFirstFrameEscape(Span<byte> dest, uint ffDl)
    {
        if (ffDl <= MaxShortFfDl)
            throw new ArgumentOutOfRangeException(nameof(ffDl),
                $"Escape encoding only valid for FF_DL > {MaxShortFfDl} (§9.6.3.2)");
        if (dest.Length < 6)
            throw new ArgumentException("Destination too small", nameof(dest));
        dest[0] = 0x10;
        dest[1] = 0x00;
        dest[2] = (byte)((ffDl >> 24) & 0xFF);
        dest[3] = (byte)((ffDl >> 16) & 0xFF);
        dest[4] = (byte)((ffDl >> 8) & 0xFF);
        dest[5] = (byte)(ffDl & 0xFF);
        return 6;
    }

    // =========================================================================
    // ConsecutiveFrame
    // =========================================================================

    /// <summary>
    /// Writes a CF N_PCI byte (§9.6.4.3, Table 17): Byte #1 hi nibble = 0x2,
    /// low nibble = SN (0..15, wraps; FF is implicitly SN=0 so first CF is SN=1).
    /// </summary>
    /// <returns>Always 1 (PCI byte count).</returns>
    public static int EncodeConsecutiveFrame(Span<byte> dest, byte sequenceNumber)
    {
        if (sequenceNumber > 0x0F)
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber),
                "SN must be 0..15 (4-bit field)");
        if (dest.Length < 1)
            throw new ArgumentException("Destination too small", nameof(dest));
        dest[0] = (byte)(0x20 | (sequenceNumber & 0x0F));
        return 1;
    }

    // =========================================================================
    // FlowControl
    // =========================================================================

    /// <summary>
    /// Writes a 3-byte FC N_PCI (§9.6.5, Table 9 row 6):
    /// Byte #1 hi nibble = 0x3, low nibble = FS; Byte #2 = BS; Byte #3 = STmin (raw).
    /// Use <see cref="STminCodec"/> to convert milliseconds/microseconds to the raw byte.
    /// </summary>
    /// <returns>Always 3 (PCI byte count).</returns>
    public static int EncodeFlowControl(Span<byte> dest, FlowStatus fs, byte bs, byte stMinRaw)
    {
        if ((byte)fs > 0x0F)
            throw new ArgumentOutOfRangeException(nameof(fs));
        if (dest.Length < 3)
            throw new ArgumentException("Destination too small", nameof(dest));
        dest[0] = (byte)(0x30 | ((byte)fs & 0x0F));
        dest[1] = bs;
        dest[2] = stMinRaw;
        return 3;
    }

    // =========================================================================
    // Decoder
    // =========================================================================

    /// <summary>
    /// Parses the N_PCI from <paramref name="frameDataField"/> (post-addressing-prefix bytes).
    /// Returns false for malformed PCI per the error-handling rules in §9.6.2.2 / §9.6.3.2 /
    /// §9.6.4.4 / §9.6.5.2 - the caller should ignore the frame in that case (no FC reply, no
    /// upper-layer notification - that's exactly what the spec mandates for "ignore" outcomes).
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> frameDataField, out NPciHeader header)
    {
        header = default;
        if (frameDataField.IsEmpty) return false;

        byte b0 = frameDataField[0];
        byte hi = (byte)(b0 >> 4);

        switch (hi)
        {
            case 0x0:   // SingleFrame
            {
                byte sfDl = (byte)(b0 & 0x0F);
                if (sfDl == 0)
                {
                    // §9.6.2.1: low nibble == 0 marks the CAN-FD escape sequence;
                    // SF_DL is in byte #2 and must be at least 8 (Table 11).
                    if (frameDataField.Length < 2) return false;
                    int escSfDl = frameDataField[1];
                    if (escSfDl < 8) return false;                // §9.6.2.2 reserved/invalid
                    header = new NPciHeader(
                        NPciType.SingleFrame, pciByteCount: 2, length: escSfDl, useEscape: true);
                    return true;
                }
                // Classical SF: SF_DL in low nibble of byte 1.
                header = new NPciHeader(
                    NPciType.SingleFrame, pciByteCount: 1, length: sfDl, useEscape: false);
                return true;
            }

            case 0x1:   // FirstFrame
            {
                if (frameDataField.Length < 2) return false;
                byte b1 = frameDataField[1];
                int shortLen = ((b0 & 0x0F) << 8) | b1;

                if (shortLen == 0)
                {
                    // §9.6.3.1 escape sequence: 32-bit FF_DL in bytes #3..#6 BE.
                    if (frameDataField.Length < 6) return false;
                    uint ffDl = ((uint)frameDataField[2] << 24)
                              | ((uint)frameDataField[3] << 16)
                              | ((uint)frameDataField[4] << 8)
                              | frameDataField[5];
                    // §9.6.3.2: an escape FF with FF_DL <= 4095 shall be ignored.
                    if (ffDl <= MaxShortFfDl) return false;
                    header = new NPciHeader(
                        NPciType.FirstFrame, pciByteCount: 6, length: (int)Math.Min(int.MaxValue, ffDl),
                        useEscape: true, length32: ffDl);
                    return true;
                }
                header = new NPciHeader(
                    NPciType.FirstFrame, pciByteCount: 2, length: shortLen, useEscape: false,
                    length32: (uint)shortLen);
                return true;
            }

            case 0x2:   // ConsecutiveFrame
                header = new NPciHeader(NPciType.ConsecutiveFrame, pciByteCount: 1, length: 0)
                {
                    SequenceNumber = (byte)(b0 & 0x0F),
                };
                return true;

            case 0x3:   // FlowControl
            {
                if (frameDataField.Length < 3) return false;
                byte fsRaw = (byte)(b0 & 0x0F);
                // §9.6.5.2: reserved FS values yield N_INVALID_FS at upper layer.
                // We surface the raw FS so the caller can apply that error path.
                header = new NPciHeader(NPciType.FlowControl, pciByteCount: 3, length: 0)
                {
                    FlowStatus = (FlowStatus)fsRaw,
                    BlockSize = frameDataField[1],
                    STminRaw = frameDataField[2],
                };
                return true;
            }

            default:
                // §9.6.1: 4..F reserved -> ignore.
                return false;
        }
    }
}

/// <summary>The four N_PDU types defined in §9.4.1.</summary>
public enum NPciType : byte
{
    SingleFrame = 0,
    FirstFrame = 1,
    ConsecutiveFrame = 2,
    FlowControl = 3,
}

/// <summary>FlowControl FlowStatus values per §9.6.5.1, Table 18. 0x3..0xF are reserved.</summary>
public enum FlowStatus : byte
{
    ContinueToSend = 0x0,
    Wait = 0x1,
    Overflow = 0x2,
}

/// <summary>
/// Parsed N_PCI header. Field meanings depend on <see cref="Type"/>:
///   SingleFrame      : Length = SF_DL; PciByteCount = 1 (classical) or 2 (escape).
///   FirstFrame       : Length32 = FF_DL; PciByteCount = 2 (short) or 6 (escape).
///                      Length is clamped to Int32.MaxValue for 32-bit FF_DL > 2^31-1.
///   ConsecutiveFrame : SequenceNumber = SN (0..15); PciByteCount = 1.
///   FlowControl      : FlowStatus, BlockSize, STminRaw populated; PciByteCount = 3.
/// </summary>
public readonly struct NPciHeader
{
    public NPciType Type { get; init; }
    public int PciByteCount { get; init; }
    public int Length { get; init; }
    public uint Length32 { get; init; }
    public bool UsesEscape { get; init; }
    public byte SequenceNumber { get; init; }
    public FlowStatus FlowStatus { get; init; }
    public byte BlockSize { get; init; }
    public byte STminRaw { get; init; }

    public NPciHeader(NPciType type, int pciByteCount, int length,
        bool useEscape = false, uint length32 = 0)
    {
        Type = type;
        PciByteCount = pciByteCount;
        Length = length;
        Length32 = length32 == 0 ? (uint)length : length32;
        UsesEscape = useEscape;
    }
}
