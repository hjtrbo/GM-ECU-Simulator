using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Transport;

namespace Core.Services;

// $3E TesterPresent. Per GMW3110 §8.15.6.2 with ISO 14229-1 sub-function compatibility:
//   - Resets the node's TesterPresent_Timer to 0 (does NOT activate the
//     state; only services that require P3C can activate it).
//   - Accepted formats:
//       [$3E]           1-byte form (GMW3110 strict)
//       [$3E $00]       ISO 14229 zeroSubFunction — what most tester stacks send
//       [$3E $80]       ISO 14229 suppressPosRspMsgIndication — reset timer, send no response
//   - Physical request -> $7E positive response (unless suppress bit set).
//   - Functional request -> silent (no response).
//   - Any other length / sub-function -> $7F $3E $12.
//
// Real-world testers (incl. the sibling DataLogger and most ISO 14229-based
// stacks) send the 2-byte form `[$3E $00]`. Rejecting it with NRC $12 makes
// the simulator unusable as a P3C keepalive target — every periodic $3E from
// the host will be NRC'd and the P3C timer will never reset.
public static class Service3EHandler
{
    public static void Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch, bool isFunctional)
    {
        if (usdtPayload.Length < 1 || usdtPayload[0] != Service.TesterPresent) return;

        bool suppressPositive = false;

        if (usdtPayload.Length == 1)
        {
            // GMW3110 1-byte form — accept as-is.
        }
        else if (usdtPayload.Length == 2)
        {
            byte sub = usdtPayload[1];
            if (sub == 0x80)
            {
                suppressPositive = true;
            }
            else if (sub != 0x00)
            {
                // Unknown sub-function — NRC $12 on physical, silent on functional.
                if (!isFunctional)
                    ServiceUtil.EnqueueNrc(node, ch, Service.TesterPresent, Nrc.SubFunctionNotSupportedInvalidFormat);
                return;
            }
            // sub == 0x00 → normal request.
        }
        else
        {
            // Invalid length — NRC $12 on physical, silent on functional.
            if (!isFunctional)
            {
                node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                    [Service.NegativeResponse, Service.TesterPresent, Nrc.SubFunctionNotSupportedInvalidFormat]);
            }
            return;
        }

        // Reset the timer regardless of address type or response suppression.
        node.State.TesterPresent.Reset();

        if (!isFunctional && !suppressPositive)
        {
            node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                [Service.Positive(Service.TesterPresent)]);
        }
    }
}
