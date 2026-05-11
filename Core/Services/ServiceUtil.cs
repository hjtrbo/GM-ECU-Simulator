using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Transport;

namespace Core.Services;

internal static class ServiceUtil
{
    // Standard GMW3110 negative-response frame: [0x7F, originalSid, nrc].
    public static void EnqueueNrc(EcuNode node, ChannelSession ch, byte sid, byte nrc)
        => IsoTpFragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.NegativeResponse, sid, nrc]);
}
