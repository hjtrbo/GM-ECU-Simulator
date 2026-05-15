using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Scheduler;
using Core.Transport;

namespace Core.Services;

// $AA ReadDataByPacketIdentifier handler. Drives the periodic DPID scheduler.
//
// USDT request:
//   byte[0]    = 0xAA
//   byte[1]    = sub-function (rate byte)
//   bytes[2..] = DPID id list
//
// Sub-functions per GMW3110 §8.20.2:
//   $00 = stopSending     (DPID list optional; empty = stop ALL)
//   $01 = sendOneResponse (emit one UUDT per DPID, no scheduling)
//   $02 = scheduleAtSlowRate
//   $03 = scheduleAtMediumRate
//   $04 = scheduleAtFastRate
//
// $AA positive responses are UUDT (DPID id + values, on the UUDT response
// CAN ID) — handled by the DpidScheduler, not echoed here.
public static class ServiceAAHandler
{
    /// <summary>Returns true if the request was a successful enhanced operation
    /// that should activate P3C. False for NRCs and for StopSending (which is
    /// silent and doesn't extend the diagnostic session).</summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch, DpidScheduler scheduler)
    {
        const byte sid = Service.ReadDataByPacketIdentifier;
        if (usdtPayload.Length < 2 || usdtPayload[0] != sid)
        {
            ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }
        var rate = (DpidRate)usdtPayload[1];

        // Collect DPID ids from bytes [2..]
        var dpidIds = new List<byte>();
        for (int i = 2; i < usdtPayload.Length; i++) dpidIds.Add(usdtPayload[i]);

        switch (rate)
        {
            case DpidRate.StopSending:
                scheduler.Stop(node, dpidIds);
                // §8.19.1.3: "The positive response to a stopSending DPID
                // request is a single UUDT diagnostic message with a value of
                // $00 in the DPID/message number position and no additional
                // data bytes." Emitted on the UUDT response CAN-ID. Does not
                // activate P3C (the spec gates P3C on enhanced ops; winding
                // the scheduler down isn't one).
                EnqueueStopAckUudt(node, ch);
                return false;

            case DpidRate.SendOneResponse:
                if (dpidIds.Count == 0)
                {
                    ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.SubFunctionNotSupportedInvalidFormat);
                    return false;
                }
                foreach (var id in dpidIds)
                {
                    if (!node.State.Dpids.TryGetValue(id, out var dpid))
                    {
                        ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.RequestOutOfRange);
                        return false;
                    }
                    scheduler.SendOnce(node, dpid, ch);
                }
                return true;

            case DpidRate.Slow:
            case DpidRate.Medium:
            case DpidRate.Fast:
                if (dpidIds.Count == 0)
                {
                    ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.SubFunctionNotSupportedInvalidFormat);
                    return false;
                }
                foreach (var id in dpidIds)
                {
                    if (!node.State.Dpids.TryGetValue(id, out var dpid))
                    {
                        ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.RequestOutOfRange);
                        return false;
                    }
                    scheduler.Add(node, dpid, ch, rate);
                }
                return true;

            default:
                ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.SubFunctionNotSupportedInvalidFormat);
                return false;
        }
    }

    // §8.19.1.3 stopSending positive response: UUDT frame with DPID=$00 and
    // no payload. Shape matches DpidScheduler.BuildUudtFrame for a hypothetical
    // empty DPID (5 bytes total: 4-byte BE CAN-ID + 1 message-number byte).
    private static void EnqueueStopAckUudt(EcuNode node, ChannelSession ch)
    {
        var frame = new byte[CanFrame.IdBytes + 1];
        CanFrame.WriteId(frame, node.UudtResponseCanId);
        frame[CanFrame.IdBytes] = 0x00;
        ch.EnqueueRx(new PassThruMsg
        {
            ProtocolID = ProtocolID.CAN,
            Data = frame,
        });
    }
}
