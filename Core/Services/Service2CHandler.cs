using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Transport;

namespace Core.Services;

// $2C DynamicallyDefineMessage handler. Tester gives us a (DPID id, list of
// PIDs) tuple; we store it in the ECU's DpidStore so subsequent $AA
// requests can reference it.
//
// USDT request:
//   byte[0]    = 0x2C
//   byte[1]    = DPID id (1..0x7F or 0x90..0xFE per GMW3110)
//   bytes[2..] = N × {PID hi, PID lo}  (N >= 1, even byte count)
//
// USDT positive response:
//   byte[0]    = 0x6C
//   byte[1]    = DPID id echo
public static class Service2CHandler
{
    /// <summary>Returns true if a positive response was enqueued (caller should
    /// activate P3C). False if an NRC was enqueued.</summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        const byte sid = Service.DynamicallyDefineMessage;
        if (usdtPayload.Length < 4 || usdtPayload[0] != sid)
        {
            ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }
        byte dpidId = usdtPayload[1];
        if (dpidId == 0x00 || (dpidId > 0x7F && dpidId < 0x90) || dpidId == 0xFF)
        {
            ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.RequestOutOfRange);
            return false;
        }

        int pidBytes = usdtPayload.Length - 2;
        if ((pidBytes & 1) != 0 || pidBytes < 2)
        {
            ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        int pidCount = pidBytes / 2;
        var pids = new Pid[pidCount];
        int totalValueBytes = 0;
        for (int i = 0; i < pidCount; i++)
        {
            ushort id = (ushort)((usdtPayload[2 + i * 2] << 8) | usdtPayload[3 + i * 2]);
            var pid = node.GetPid(id);
            if (pid == null)
            {
                ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.RequestOutOfRange);
                return false;
            }
            pids[i] = pid;
            totalValueBytes += (int)pid.Size;
        }

        // UUDT frame carries 7 data bytes after the DPID id byte (CAN frame
        // is 8 bytes total: 1 byte id + 7 bytes payload).
        if (totalValueBytes > 7)
        {
            ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        node.State.AddDpid(new Dpid { Id = dpidId, Pids = pids });

        IsoTpFragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(sid), dpidId]);
        return true;
    }
}
