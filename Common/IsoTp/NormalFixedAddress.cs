namespace Common.IsoTp;

/// <summary>
/// ISO 15765-2:2016 §10.3.3 Normal Fixed addressing. Encodes/decodes the
/// 29-bit CAN identifier from N_SA, N_TA and N_TAtype:
///
///   bits 28..26 : Priority (default 6 = 110b)
///   bit 25      : Reserved (0)
///   bit 24      : Data page (0)
///   bits 23..16 : PF (218 = 0xDA physical, 219 = 0xDB functional)
///   bits 15..8  : N_TA (target address)
///   bits 7..0   : N_SA (source address)
///
/// The PF byte is the only thing that distinguishes physical from functional;
/// see Table 26 (physical) and Table 27 (functional). Higher (priority) bits
/// must be ignored on receive per §10.3.3 / Annex A.2.3.
/// </summary>
public static class NormalFixedAddress
{
    /// <summary>PF byte for physical addressing (PGN 0xDA00).</summary>
    public const byte PfPhysical = 0xDA;
    /// <summary>PF byte for functional addressing (PGN 0xDB00).</summary>
    public const byte PfFunctional = 0xDB;
    /// <summary>Default 3-bit priority (§10.3.3 / A.2.3).</summary>
    public const byte DefaultPriority = 6;

    /// <summary>
    /// Builds a 29-bit CAN ID for normal-fixed addressing. Use
    /// <see cref="PfPhysical"/> or <see cref="PfFunctional"/> for <paramref name="pf"/>.
    /// </summary>
    public static uint BuildId(byte priority, byte pf, byte nTa, byte nSa)
    {
        if (priority > 7)
            throw new ArgumentOutOfRangeException(nameof(priority), "Priority is 3 bits");
        return ((uint)(priority & 0x07) << 26)
             | ((uint)pf << 16)
             | ((uint)nTa << 8)
             | nSa;
    }

    /// <summary>
    /// Builds a 29-bit physical-addressed CAN ID with the spec's default priority.
    /// </summary>
    public static uint BuildPhysical(byte nTa, byte nSa)
        => BuildId(DefaultPriority, PfPhysical, nTa, nSa);

    /// <summary>
    /// Builds a 29-bit functionally-addressed CAN ID with the spec's default priority.
    /// </summary>
    public static uint BuildFunctional(byte nTa, byte nSa)
        => BuildId(DefaultPriority, PfFunctional, nTa, nSa);

    /// <summary>
    /// Splits a 29-bit CAN ID. Returns true if the PF byte matches one of the
    /// reserved normal-fixed values; false means the ID is some other 29-bit
    /// frame and should be ignored by the normal-fixed receiver per §10.3.3.
    /// </summary>
    public static bool TryParse(uint canId29, out byte pf, out byte nTa, out byte nSa, out bool isFunctional)
    {
        pf = (byte)((canId29 >> 16) & 0xFF);
        nTa = (byte)((canId29 >> 8) & 0xFF);
        nSa = (byte)(canId29 & 0xFF);
        isFunctional = pf == PfFunctional;
        return pf is PfPhysical or PfFunctional;
    }
}
