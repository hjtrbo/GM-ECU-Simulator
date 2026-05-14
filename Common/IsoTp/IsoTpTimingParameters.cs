namespace Common.IsoTp;

/// <summary>
/// Per-N_AI timing parameters from ISO 15765-2:2016 §9.8.1 / Table 21.
/// All values are in milliseconds (the spec encodes STmin as a single byte
/// inside FC frames, but we carry the decoded ms value here for the timer
/// budget; <see cref="STminCodec"/> handles the wire encoding).
///
/// The defaults match the spec timeout maxima (1000 ms for the receive-side
/// timers). Real systems can shorten these via the J2534 CONFIG_PARAMETER
/// IDs (ISO15765_BS, ISO15765_STMIN, etc.). Per §9.8.1 the actual timeout
/// must occur "no later than at the specified timeout value + 50 %".
/// </summary>
public sealed class IsoTpTimingParameters
{
    /// <summary>
    /// N_As (sender CAN frame TX): time from L_Data.request to L_Data.confirm
    /// for any N_PDU on the sender side. Spec timeout = 1000 ms (Table 21).
    /// </summary>
    public int NAsMs { get; set; } = 1000;

    /// <summary>
    /// N_Ar (receiver CAN frame TX): time from L_Data.request to
    /// L_Data.confirm for any N_PDU on the receiver side. Spec timeout = 1000 ms.
    /// </summary>
    public int NArMs { get; set; } = 1000;

    /// <summary>
    /// N_Bs (sender wait for FC): time until the next FC N_PDU is received
    /// after sending an FF or the last CF of a block. Spec timeout = 1000 ms.
    /// </summary>
    public int NBsMs { get; set; } = 1000;

    /// <summary>
    /// N_Br (receiver delay before sending FC): time from L_Data.indication
    /// (FF or CF) to L_Data.request (FC). Per §9.8.1 there is no spec timeout
    /// for N_Br; the constraint is the performance requirement
    /// (N_Br + N_Ar) &lt; 0.9 * N_Bs_timeout. Default 0 ms.
    /// </summary>
    public int NBrMs { get; set; } = 0;

    /// <summary>
    /// N_Cs (sender STmin compliance): time from receiving an FC to sending
    /// the next CF. No spec timeout (constraint: (N_Cs + N_As) &lt; 0.9 *
    /// N_Cr_timeout). The runtime value is whichever is larger of the
    /// remembered STmin (from the most recent FC) and this configured floor.
    /// Default 0 ms.
    /// </summary>
    public int NCsMs { get; set; } = 0;

    /// <summary>
    /// N_Cr (receiver wait for next CF): time until the next CF is received.
    /// Spec timeout = 1000 ms.
    /// </summary>
    public int NCrMs { get; set; } = 1000;

    /// <summary>
    /// STmin we send in our FC frames as a receiver (raw byte form). 0x00 =
    /// no separation. Senders that observe an STmin in (0xF1..0xF9) must
    /// honour it as 100..900 us; we default to 0 (back-to-back).
    /// </summary>
    public byte StMinSendRaw { get; set; } = 0;

    /// <summary>
    /// BlockSize we send in our FC frames as a receiver. 0 = no further FC
    /// frames will be sent; the sender may transmit all remaining CFs.
    /// </summary>
    public byte BlockSizeSend { get; set; } = 0;

    /// <summary>
    /// Maximum FC.WAIT frames a receiver may emit consecutively (§9.7).
    /// 0 disables FC.WAIT entirely; the receiver shall use FC.CTS only.
    /// </summary>
    public int NWftMax { get; set; } = 0;

    public IsoTpTimingParameters Clone() => new()
    {
        NAsMs = NAsMs,
        NArMs = NArMs,
        NBsMs = NBsMs,
        NBrMs = NBrMs,
        NCsMs = NCsMs,
        NCrMs = NCrMs,
        StMinSendRaw = StMinSendRaw,
        BlockSizeSend = BlockSizeSend,
        NWftMax = NWftMax,
    };
}
