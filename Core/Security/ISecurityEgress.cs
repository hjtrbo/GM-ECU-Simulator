namespace Core.Security;

// Frame egress for security modules. Wraps the simulator's ISO-TP sender
// so the module never imports Core.Transport. Service27Handler supplies
// the concrete implementation, prefixing the response SID ($67) and the
// ECU's UsdtResponseCanId as appropriate.
public interface ISecurityEgress
{
    /// <summary>
    /// Send a positive response: $67, subfunction byte, then the supplied
    /// data bytes (the seed for requestSeed, empty for sendKey success).
    /// </summary>
    void SendPositiveResponse(byte subfunction, ReadOnlySpan<byte> data);

    /// <summary>Send a negative response: $7F, $27, nrc.</summary>
    void SendNegativeResponse(byte nrc);

    /// <summary>
    /// Escape hatch — send an arbitrary USDT payload. The bytes are
    /// fragmented through ISO-TP and enqueued on the ECU's response
    /// CAN ID exactly as written. For non-standard protocol flows only;
    /// prefer SendPositiveResponse / SendNegativeResponse.
    /// </summary>
    void SendRaw(ReadOnlySpan<byte> usdtPayload);
}
