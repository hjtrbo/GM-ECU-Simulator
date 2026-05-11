using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Transport;

namespace Core.Services;

// $22 ReadDataByParameterIdentifier handler. Now operates on assembled USDT
// payloads (post-PCI). Supports multi-PID requests; the response can be
// fragmented across multiple ISO-TP frames if it exceeds 7 bytes.
//
// USDT request:
//   byte[0]    = 0x22
//   bytes[1..] = N × {PID hi, PID lo}
//
// USDT response:
//   byte[0]    = 0x62
//   bytes[1..] = N × {PID hi, PID lo, value bytes (size from Pid.Size)}
public static class Service22Handler
{
    public static void Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch, double timeMs)
    {
        // SID + at least one PID pair.
        if (usdtPayload.Length < 3 || usdtPayload[0] != Service.ReadDataByParameterIdentifier)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.ReadDataByParameterIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }
        int pidBytes = usdtPayload.Length - 1;
        if ((pidBytes & 1) != 0)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.ReadDataByParameterIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }
        int pidCount = pidBytes / 2;

        // Validate every PID exists before building any of the response. If any
        // is unsupported, return NRC $31 RequestOutOfRange (per GMW3110 §8.6.4).
        var pids = new Pid[pidCount];
        for (int i = 0; i < pidCount; i++)
        {
            ushort pidId = (ushort)((usdtPayload[1 + i * 2] << 8) | usdtPayload[2 + i * 2]);
            var pid = node.GetPid(pidId);
            if (pid == null)
            {
                ServiceUtil.EnqueueNrc(node, ch, Service.ReadDataByParameterIdentifier, Nrc.RequestOutOfRange);
                return;
            }
            pids[i] = pid;
        }

        // Compute response size and allocate.
        int respSize = 1;                           // SID
        foreach (var pid in pids) respSize += 2 + (int)pid.Size;
        var resp = new byte[respSize];
        resp[0] = Service.Positive(Service.ReadDataByParameterIdentifier);
        int pos = 1;
        foreach (var pid in pids)
        {
            resp[pos++] = (byte)(pid.Address >> 8);
            resp[pos++] = (byte)(pid.Address & 0xFF);
            ValueCodec.Encode(
                pid.Waveform.Sample(timeMs),
                pid.Scalar, pid.Offset, pid.DataType, (int)pid.Size,
                resp.AsSpan(pos, (int)pid.Size));
            pos += (int)pid.Size;
        }

        IsoTpFragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, resp);
    }
}
