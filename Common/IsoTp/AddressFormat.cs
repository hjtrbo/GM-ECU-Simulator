namespace Common.IsoTp;

/// <summary>
/// ISO 15765-2:2016 §10.3 addressing formats.
///
/// The format dictates how N_AI parameters (N_SA, N_TA, N_TAtype, optional
/// N_AE) are mapped onto the CAN identifier and the leading bytes of the
/// CAN frame data field. It also determines how many bytes of N_PCI + N_Data
/// fit in a single frame.
/// </summary>
public enum AddressFormat
{
    /// <summary>
    /// §10.3.2. The CAN identifier carries the full N_AI; N_PCI begins at
    /// data byte 1. Maximum SF_DL with classical CAN = 7 bytes.
    /// </summary>
    Normal = 0,

    /// <summary>
    /// §10.3.3. Subformat of normal addressing where N_SA and N_TA are
    /// embedded in fixed positions of a 29-bit CAN identifier (PGN 0xDA = 218
    /// physical, 0xDB = 219 functional, priority default 6, R=0, DP=0).
    /// Frame data field layout matches Normal (N_PCI begins at byte 1).
    /// </summary>
    NormalFixed = 1,

    /// <summary>
    /// §10.3.4. The first byte of the CAN frame data field carries N_TA;
    /// N_PCI begins at data byte 2. Maximum SF_DL with classical CAN = 6 bytes.
    /// </summary>
    Extended = 2,

    /// <summary>
    /// §10.3.5. The first byte of the CAN frame data field carries N_AE;
    /// N_PCI begins at data byte 2. Used when Mtype = remote diagnostics.
    /// 11-bit and 29-bit CAN ID variants exist; data-field layout is identical.
    /// Maximum SF_DL with classical CAN = 6 bytes.
    /// </summary>
    Mixed = 3,
}

/// <summary>
/// Per-frame layout helpers for an <see cref="AddressFormat"/>. Encapsulates
/// the "1 vs 2 byte addressing prefix in the CAN data field" distinction that
/// drives every length calculation in §9.6.
/// </summary>
public static class AddressFormatLayout
{
    /// <summary>
    /// Number of CAN data bytes consumed by the address prefix before N_PCI
    /// begins. Normal/NormalFixed = 0 (address is in the CAN ID); Extended
    /// and Mixed = 1 (N_TA / N_AE is the first data byte).
    /// </summary>
    public static int AddressPrefixBytes(AddressFormat fmt) => fmt switch
    {
        AddressFormat.Normal or AddressFormat.NormalFixed => 0,
        AddressFormat.Extended or AddressFormat.Mixed => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(fmt)),
    };

    /// <summary>
    /// Maximum SF_DL when CAN_DL == 8 for the given format (§9.6.2.1, Table 10).
    /// 7 for normal, 6 for extended/mixed.
    /// </summary>
    public static int MaxClassicalSfDl(AddressFormat fmt)
        => 7 - AddressPrefixBytes(fmt);

    /// <summary>
    /// Bytes of N_Data that fit in the first frame when CAN_DL == 8
    /// (§9.6.3.1). 6 for normal, 5 for extended/mixed.
    /// </summary>
    public static int MaxClassicalFfData(AddressFormat fmt)
        => 6 - AddressPrefixBytes(fmt);

    /// <summary>
    /// Bytes of N_Data that fit in a consecutive frame when CAN_DL == 8
    /// (§9.6.4.1). 7 for normal, 6 for extended/mixed.
    /// </summary>
    public static int MaxClassicalCfData(AddressFormat fmt)
        => 7 - AddressPrefixBytes(fmt);

    /// <summary>
    /// FF_DL_min from §9.6.3.1, Table 14. The receiver uses this lower bound
    /// to ignore a malformed FF that should have been an SF.
    /// </summary>
    public static int FfDlMinClassical(AddressFormat fmt)
        => 8 - AddressPrefixBytes(fmt);

    /// <summary>
    /// Writes the address prefix (if any) into the start of the CAN frame
    /// data field. Returns the number of bytes written - this is the index
    /// at which N_PCI must begin.
    /// </summary>
    public static int WriteAddressPrefix(Span<byte> canData, AddressFormat fmt, byte nTaOrNAe)
    {
        int n = AddressPrefixBytes(fmt);
        if (n == 0) return 0;
        if (canData.Length < 1) throw new ArgumentException("Destination too small", nameof(canData));
        canData[0] = nTaOrNAe;
        return n;
    }

    /// <summary>
    /// Reads the address prefix byte (N_TA for Extended, N_AE for Mixed) and
    /// returns a slice of the data field starting at the first N_PCI byte.
    /// For Normal/NormalFixed the prefix byte is undefined and the slice is
    /// the input unchanged.
    /// </summary>
    public static ReadOnlySpan<byte> StripAddressPrefix(
        ReadOnlySpan<byte> canData, AddressFormat fmt, out byte prefixByte)
    {
        int n = AddressPrefixBytes(fmt);
        if (n == 0)
        {
            prefixByte = 0;
            return canData;
        }
        if (canData.IsEmpty) { prefixByte = 0; return default; }
        prefixByte = canData[0];
        return canData[1..];
    }
}
