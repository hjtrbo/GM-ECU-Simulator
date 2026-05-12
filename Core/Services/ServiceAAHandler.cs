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
                // No response per GMW3110 — $AA $00 is silent and does not
                // activate P3C (it's the opposite — winding down activity).
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
}
