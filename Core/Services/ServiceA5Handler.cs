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
public static class ServiceA5Handler
{
    /// <summary>
    /// Returns true if a positive response was sent OR if $03 enabled programming
    /// mode (caller activates P3C in either case so the new session keeps the timer
    /// fresh). False if an NRC was sent.
    /// </summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        if (usdtPayload.Length != 2 || usdtPayload[0] != Service.ProgrammingMode)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.ProgrammingMode, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }
        byte sub = usdtPayload[1];

        // §8.17.4: programming-mode-already-active maps to $22, not $12.
        if (node.State.ProgrammingModeActive)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.ProgrammingMode, Nrc.ConditionsNotCorrectOrSequenceError);
            return false;
        }

        switch (sub)
        {
            case 0x01:   // requestProgrammingMode (normal speed)
            case 0x02:   // requestProgrammingMode_HighSpeed
                if (!node.State.NormalCommunicationDisabled)
                {
                    // §8.17.4 NRC $22: $28 not active.
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
                    // §8.17.4 NRC $22: prerequisites not met. Note: spec says no
                    // response to $03 even on success - it's debatable whether
                    // the NRC suppression also applies here. We DO send the NRC
                    // so the tester can diagnose the sequence error; spec is
                    // silent on this corner case.
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
                ServiceUtil.EnqueueNrc(node, ch, Service.ProgrammingMode, Nrc.SubFunctionNotSupportedInvalidFormat);
                return false;
        }
    }
}
