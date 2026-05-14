using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// $28 DisableNormalCommunication. GMW3110-2010 §8.9 (p137-139).
//
// Request: just SID $28 (length must == 1). No sub-function, no data.
// Positive response: $68.
// NRCs:
//   $12 SubFunctionNotSupportedInvalidFormat - length != 1
//   $22 ConditionsNotCorrect - device cannot disable now (we never gate on this)
//
// Side effects per spec (§8.9.6.2):
//   - normal_message_transmission_status -> DISABLED
//   - Diag_Services_Disable_DTCs -> TRUE
//   - TesterPresent_Timer_State -> ACTIVE (handled by ActivateP3C in dispatcher)
//
// Functional broadcast use ($101 / $FE) is the typical programming-event entry
// per §8.9.5.1, but the request/response shape is the same physically. The
// dispatcher handles the functional/physical split before this handler runs.
public static class Service28Handler
{
    /// <summary>
    /// Returns true if a positive response was sent (caller activates P3C),
    /// false if an NRC was sent.
    /// </summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch, bool isFunctional)
    {
        if (usdtPayload.Length != 1 || usdtPayload[0] != Service.DisableNormalCommunication)
        {
            // §8.9.6.2: the spec only gates the NRC on diagnostic_responses_enabled
            // (silent on functional broadcast for malformed). Mirror that.
            if (!isFunctional)
                ServiceUtil.EnqueueNrc(node, ch, Service.DisableNormalCommunication, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        node.State.NormalCommunicationDisabled = true;

        // Functional broadcast: respond too (§8.9.5.1 example shows N1..Nn each
        // sending a $68 SF in response to the $101 functional $28).
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(Service.DisableNormalCommunication)]);
        return true;
    }
}
