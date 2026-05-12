using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Security;
using Core.Transport;

namespace Core.Services;

// $27 SecurityAccess dispatcher. The dispatcher only handles the routing:
//   - No module configured on the ECU → NRC $11 ServiceNotSupported.
//   - Module configured → wrap the channel as an ISecurityEgress, build a
//     SecurityAccessContext, hand off to module.Handle().
// All protocol / algorithm decisions live in the module (typically the
// bundled Gmw3110_2010_Generic + an injected ISeedKeyAlgorithm).
//
// $27 activates P3C — same shape as Service10Handler. The dispatcher
// returns true when the module ran, regardless of positive/negative
// response: even a failed $27 attempt counts as enhanced traffic that
// should refresh the keepalive window.
public static class Service27Handler
{
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch, long nowMs)
    {
        var module = node.SecurityModule;
        if (module is null)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.SecurityAccess, Nrc.ServiceNotSupported);
            return false;
        }

        var egress = new ChannelEgress(node, ch);
        var ctx = new SecurityAccessContext
        {
            Node = node,
            Channel = ch,
            UsdtPayload = usdtPayload,
            State = node.State,
            NowMs = nowMs,
            Egress = egress,
        };
        module.Handle(ctx);
        return true;
    }

    // Wraps IsoTpFragmenter for the security module. Keeps the transport
    // layer out of the module's import graph.
    private sealed class ChannelEgress(EcuNode node, ChannelSession channel) : ISecurityEgress
    {
        public void SendPositiveResponse(byte subfunction, ReadOnlySpan<byte> data)
        {
            // [0x67, sub, ...data]
            var payload = new byte[2 + data.Length];
            payload[0] = Service.Positive(Service.SecurityAccess);
            payload[1] = subfunction;
            data.CopyTo(payload.AsSpan(2));
            IsoTpFragmenter.EnqueueResponse(channel, node.UsdtResponseCanId, payload);
        }

        public void SendNegativeResponse(byte nrc)
            => ServiceUtil.EnqueueNrc(node, channel, Service.SecurityAccess, nrc);

        public void SendRaw(ReadOnlySpan<byte> usdtPayload)
            => IsoTpFragmenter.EnqueueResponse(channel, node.UsdtResponseCanId, usdtPayload);
    }
}
