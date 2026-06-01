using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// $22 ReadDataByParameterIdentifier handler. Operates on assembled USDT
// payloads (post-PCI). Supports multi-PID requests; the response can be
// fragmented across multiple ISO-TP frames if it exceeds 7 bytes.
//
// USDT request:
//   byte[0]    = 0x22
//   bytes[1..] = N × {PID hi, PID lo}
//
// USDT response:
//   byte[0]    = 0x62
//   bytes[1..] = K × {PID hi, PID lo, value bytes (size from Pid.Size)}
//   where K is the count of *supported* PIDs from the request.
//
// Per GMW3110 §8.6.1: "If a tester requests multiple PIDs with a single
// request of this service, the ECU shall include data in a positive response
// for all of the PIDs that it supports. No data shall be included in a
// positive response for unsupported PIDs". And §8.6.4 NRC $31 fires only on
// a *physical* request when **none** of the requested PIDs are supported;
// a functional request with no supported PIDs gets no response at all.
//
// Real-silicon note (E38 12647991 / E67 12656942, static analysis 2026-05-19):
// $22 is in the GMW3110-2010 PDF but on the surveyed bins it lives ONLY on the
// UDS-stack dispatcher reached via OBD CAN IDs $7DF/$7E0/$101; the GMW3110
// GMLAN-enhanced-diag dispatcher returns NRC $11 for $22. The PID-by-DID table
// at E38 $145718 is the $22 data source (see Mode22DidBinExtractor).
public static class Service22Handler
{
    public static void Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch, double timeMs, bool isFunctional)
    {
        // SID + at least one PID pair.
        if (usdtPayload.Length < 3 || usdtPayload[0] != Service.ReadDataByParameterIdentifier)
        {
            if (!isFunctional)
                ServiceUtil.EnqueueNrc(node, ch, Service.ReadDataByParameterIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }
        int pidBytes = usdtPayload.Length - 1;
        if ((pidBytes & 1) != 0)
        {
            if (!isFunctional)
                ServiceUtil.EnqueueNrc(node, ch, Service.ReadDataByParameterIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }
        int pidCount = pidBytes / 2;

        // §8.6.1: filter to the subset of supported PIDs. Unsupported entries
        // are silently skipped; the response includes only the PIDs the ECU
        // actually knows. Order is preserved from the request.
        var supported = new List<(ushort WireId, Pid Pid)>(pidCount);
        for (int i = 0; i < pidCount; i++)
        {
            ushort pidId = (ushort)((usdtPayload[1 + i * 2] << 8) | usdtPayload[2 + i * 2]);
            // GetPidByWireId resolves Mode22 (Address-as-PID) and Mode2D
            // (Address-as-memory-addr, alias derived) in one pass; Mode1A
            // rows aren't reachable here and won't match.
            var pid = node.GetPidByWireId(pidId);
            if (pid != null) supported.Add((pidId, pid));
        }

        if (supported.Count == 0)
        {
            // §8.6.4: physical => NRC $31; functional => silent.
            if (!isFunctional)
                ServiceUtil.EnqueueNrc(node, ch, Service.ReadDataByParameterIdentifier, Nrc.RequestOutOfRange);
            return;
        }

        int respSize = 1;                                   // SID
        foreach (var (_, pid) in supported) respSize += 2 + pid.ResponseLength;
        var resp = new byte[respSize];
        resp[0] = Service.Positive(Service.ReadDataByParameterIdentifier);
        int pos = 1;
        foreach (var (wireId, pid) in supported)
        {
            // Echo the wire-side id, not Pid.Address: for Mode2D rows the
            // Address is a 32-bit memory address and the wire id is the
            // derived alias.
            resp[pos++] = (byte)(wireId >> 8);
            resp[pos++] = (byte)(wireId & 0xFF);
            int len = pid.ResponseLength;
            pid.WriteResponseBytes(timeMs, resp.AsSpan(pos, len));
            pos += len;
        }

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, resp);
    }
}
