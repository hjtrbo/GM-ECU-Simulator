using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// $A5 ProgrammingMode. GMW3110-2010 §8.17 (p186-194).
//
// Request: SID $A5 + sub-function (length must == 2).
// Sub-functions (§8.17.2.1, Table 166):
//   $01 requestProgrammingMode             - normal-speed verification step
//   $02 requestProgrammingMode_HighSpeed   - high-speed verification (SWCAN only; treat as normal here)
//   $03 enableProgrammingMode              - actually enter programming session (NO response sent)
// Positive response: $E5 (only for $01 / $02).
//
// NRCs (§8.17.4, Table 168):
//   $12 SFNS-IF       - sub-function invalid AND programming mode not already active
//   $12 SFNS-IF       - length incorrect
//   $22 CNCRSE        - $03 received without prior $01/$02 (sequence)
//                       OR $28 not active
//                       OR programming mode already active
//                       OR device cannot enter (operating conditions; we don't gate)
//
// Per §8.17.6.2 pseudo-code, $03 with no prior $01/$02 is a sequence error
// ($22 CNCRSE). We track ProgrammingModeRequested for this check.
//
// Addressing: §8.17.5.1 Table 169 shows the canonical wire pattern - $A5 is
// sent on functional broadcast ($101/$FE) and each programmable node responds
// on its physical USDT response ID. The DPS PM page 241 wire trace matches.
// When called functionally we suppress all NRC emission so a single tester
// broadcast doesn't trigger every silent ECU on the link to NRC simultaneously.
public static class ServiceA5Handler
{
    /// <summary>
    /// Returns true if a positive response was sent OR if $03 enabled programming
    /// mode (caller activates P3C in either case so the new session keeps the timer
    /// fresh). False if an NRC was sent or the request was silently dropped on a
    /// functional broadcast.
    /// </summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch,
                              bool isFunctional = false)
    {
        if (usdtPayload.Length != 2 || usdtPayload[0] != Service.ProgrammingMode)
        {
            if (!isFunctional)
                ServiceUtil.EnqueueNrc(node, ch, Service.ProgrammingMode, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }
        byte sub = usdtPayload[1];

        // §8.17.4: programming-mode-already-active maps to $22, not $12.
        if (node.State.ProgrammingModeActive)
        {
            if (!isFunctional)
                ServiceUtil.EnqueueNrc(node, ch, Service.ProgrammingMode, Nrc.ConditionsNotCorrectOrSequenceError);
            return false;
        }

        switch (sub)
        {
            case 0x01:   // requestProgrammingMode (normal speed)
            case 0x02:   // requestProgrammingMode_HighSpeed
                if (!node.State.NormalCommunicationDisabled)
                {
                    // §8.17.4 NRC $22: $28 not active. Functional broadcast
                    // silently dropped - per §8.17 every receiving node is
                    // expected to participate; nodes that didn't see $28 just
                    // stay quiet rather than blanketing the bus with NRCs.
                    if (!isFunctional)
                        ServiceUtil.EnqueueNrc(node, ch, Service.ProgrammingMode, Nrc.ConditionsNotCorrectOrSequenceError);
                    return false;
                }
                node.State.ProgrammingModeRequested = true;
                node.State.ProgrammingHighSpeed = (sub == 0x02);
                node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                    [Service.Positive(Service.ProgrammingMode)]);
                return true;

            case 0x03:   // enableProgrammingMode
                if (!node.State.ProgrammingModeRequested || !node.State.NormalCommunicationDisabled)
                {
                    // §8.17.3 footnote M2 says no response to $03 in any case,
                    // but §8.17.6.2 pseudo-code lists NRC $22 on sequence error.
                    // Reconcile: emit the NRC only on physical requests so a
                    // tester can diagnose a misordered point-to-point sequence;
                    // suppress on functional so the bus doesn't echo $7F A5 22
                    // from every node that didn't see $A5 $01 first.
                    if (!isFunctional)
                        ServiceUtil.EnqueueNrc(node, ch, Service.ProgrammingMode, Nrc.ConditionsNotCorrectOrSequenceError);
                    return false;
                }
                node.State.ProgrammingModeActive = true;
                // The full GMW3110 entry also opens the security-shortcut door
                // so the $27 module's BypassAll policy fires the same way it
                // would for the UDS-style $10 $02 shortcut.
                node.State.SecurityProgrammingShortcutActive = true;
                // §8.17.3 footnote M2: "There is no response to a request message
                // with a sub-parameter value of $03." Returning true so the
                // dispatcher activates P3C.
                return true;

            default:
                // §8.17.4: sub-function invalid AND programming mode NOT active -> $12.
                if (!isFunctional)
                    ServiceUtil.EnqueueNrc(node, ch, Service.ProgrammingMode, Nrc.SubFunctionNotSupportedInvalidFormat);
                return false;
        }
    }
}
