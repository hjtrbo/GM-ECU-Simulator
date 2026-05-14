using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// $36 TransferData. GMW3110-2010 §8.13 (p161-167).
//
// Request: SID $36 + sub-function ($00 Download or $80 DownloadAndExecute)
//          + startingAddress (2..4 bytes BE) + dataRecord (variable).
// Positive response: $76 (no data parameters).
//
// NRCs (§8.13.4, Table 142):
//   $12 SFNS-IF   - sub-function invalid, or length too short for sub-function
//   $22 CNCRSE    - TransferData_Allowed = NO (i.e., $34 not run first)
//   $31 ROOR      - startingAddress invalid, or start + length exceeds buffer
//   $78 RCR-RP    - cannot process within P2C (we never need this)
//   $83 VOLTRNG   - voltage out of range (we never need this)
//   $85 PROGFAIL  - program/erase/CRC failure (we never need this)
//
// The startingAddress byte-count is fixed per ECU and must match across all
// $36 calls in the same session (§8.13.2 Note 1). We use the value frozen by
// the most recent $34 in NodeState.DownloadAddressByteCount.
//
// Our simulator treats startingAddress as an OFFSET into the sink buffer
// (rather than a literal memory address). For a tester that sends a single
// $36 with startingAddress = 0 and the full payload, this is a perfect
// round-trip; testers that segment by address get exactly the same effect
// as if the buffer were memory-mapped at base 0.
public static class Service36Handler
{
    /// <summary>Returns true if a positive response was sent, false if an NRC was sent.</summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        int addrBytes = node.State.DownloadAddressByteCount;
        // Minimum length: 1 SID + 1 sub + addrBytes + 1 data byte (sub $00 requires data).
        // For sub $80, data may be empty.
        if (usdtPayload.Length < 2 + addrBytes || usdtPayload[0] != Service.TransferData)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        byte sub = usdtPayload[1];
        if (sub != 0x00 && sub != 0x80)
        {
            // §8.13.2.1: only $00 (Download) and $80 (DownloadAndExecute) are defined.
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        // §8.13.4: $00 requires at least one data byte.
        if (sub == 0x00 && usdtPayload.Length == 2 + addrBytes)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        if (!node.State.DownloadActive || node.State.DownloadBuffer is null)
        {
            // §8.13.4 NRC $22: $34 not run / TransferData_Allowed = NO.
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.ConditionsNotCorrectOrSequenceError);
            return false;
        }

        // Decode startingAddress as a BE unsigned integer.
        uint startingAddress = 0;
        for (int i = 0; i < addrBytes; i++)
            startingAddress = (startingAddress << 8) | usdtPayload[2 + i];

        var dataRecord = usdtPayload.Slice(2 + addrBytes);

        // §8.13.4 NRC $31: out-of-range. We treat startingAddress as offset into
        // the declared buffer and reject anything past the end.
        long endExclusive = (long)startingAddress + dataRecord.Length;
        if (endExclusive > node.State.DownloadBuffer.Length)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.RequestOutOfRange);
            return false;
        }

        if (dataRecord.Length > 0)
        {
            dataRecord.CopyTo(node.State.DownloadBuffer.AsSpan((int)startingAddress));
            node.State.DownloadBytesReceived += (uint)dataRecord.Length;
        }

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(Service.TransferData)]);
        return true;
    }
}
