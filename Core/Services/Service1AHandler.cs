using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Transport;

namespace Core.Services;

// $1A ReadDataByIdentifier per GMW3110-2010 §8.3.
//
// USDT request:
//   byte[0] = 0x1A
//   byte[1] = dataIdentifier (DID), e.g. $90 = VIN
//
// USDT positive response (§8.3.5.1):
//   byte[0]    = 0x5A
//   byte[1]    = echoed dataIdentifier
//   bytes[2..] = identifier value (length defined per-DID by the spec)
//
// Negative responses (§8.3.5.2):
//   $7F 1A 12   SubFunctionNotSupported-InvalidFormat — request length != 2
//   $7F 1A 31   RequestOutOfRange — DID is not configured on this ECU
//
// Response payload size is unbounded by the SID itself; multi-byte DIDs such
// as VIN ($90, 17 ASCII bytes) need an ISO-TP First Frame + Consecutive
// Frames. The fragmenter handles that transparently when EnqueueResponse is
// called with a >7 byte payload.
public static class Service1AHandler
{
    public static void Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        if (usdtPayload.Length != 2 || usdtPayload[0] != Service.ReadDataByIdentifier)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.ReadDataByIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        byte did = usdtPayload[1];
        var data = node.GetIdentifier(did);
        if (data == null)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.ReadDataByIdentifier, Nrc.RequestOutOfRange);
            return;
        }

        var resp = new byte[2 + data.Length];
        resp[0] = Service.Positive(Service.ReadDataByIdentifier);
        resp[1] = did;
        data.CopyTo(resp.AsSpan(2));

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, resp);
    }
}
