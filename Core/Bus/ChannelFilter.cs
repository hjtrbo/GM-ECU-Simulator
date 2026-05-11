using Common.PassThru;

namespace Core.Bus;

// J2534 message filter — the host registers up to N of these per channel
// to control which incoming frames are delivered. We honour Mask/Pattern on
// the Rx-to-host path (frames the simulator pushes UP). We do not enforce
// filters on the Tx-from-host path because the simulator already routes by
// destination CAN ID directly.
public sealed class ChannelFilter
{
    public required uint Id { get; init; }
    public required FilterType Type { get; init; }
    public byte[] Mask { get; init; } = [];
    public byte[] Pattern { get; init; } = [];

    // Returns true if `frame` data passes this filter's match logic.
    // For PASS_FILTER the frame must match; for BLOCK_FILTER a match means
    // discard. FLOW_CONTROL is a special case in raw-CAN we treat as pass.
    public bool Matches(ReadOnlySpan<byte> frame)
    {
        int n = Math.Min(Math.Min(Mask.Length, Pattern.Length), frame.Length);
        for (int i = 0; i < n; i++)
            if ((frame[i] & Mask[i]) != (Pattern[i] & Mask[i]))
                return false;
        return true;
    }
}
