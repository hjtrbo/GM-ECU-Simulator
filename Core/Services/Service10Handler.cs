using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Transport;

namespace Core.Services;

// $10 InitiateDiagnosticOperation. GMW3110-2010 §8.4 lists sub $02
// disableAllDTCs, $03 enableDTCsDuringDeviceControl, $04 wakeUpLinks.
// We don't model DTC behaviour, so for those subs we just echo and
// activate P3C.
//
// $10 $02 is also the UDS DiagnosticSessionControl "programmingSession"
// entry, and real GM ECUs (T43 TCM in particular) accept it as a
// shortcut into the security path of programming session. 6Speed.T43
// relies on this: its wire trace is $10 $02 -> $27 $01 -> ... and never
// sends the full $28 + $A5 chain. We flip SecurityProgrammingShortcutActive
// on $02 so the security module's per-algorithm ProgrammingSessionBehavior
// can short-circuit $27 (T43 BypassAll behaviour). The full $34 download
// path still requires the strict GMW3110 chain ($28 -> $A5 $01 -> $A5 $03 ->
// ProgrammingModeActive); this shortcut only opens the security door,
// matching how real T43 hardware behaves.
public static class Service10Handler
{
    /// <summary>Returns true if a positive response was enqueued (caller should
    /// activate P3C). False if an NRC was enqueued.</summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        if (usdtPayload.Length != 2 || usdtPayload[0] != Service.InitiateDiagnosticOperation)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.InitiateDiagnosticOperation, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }
        byte sub = usdtPayload[1];

        if (sub == 0x02)
        {
            // UDS-style programmingSession entry. Cleared by EcuExitLogic on
            // $20 / P3C timeout via ClearProgrammingState.
            node.State.SecurityProgrammingShortcutActive = true;
        }

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(Service.InitiateDiagnosticOperation), sub]);
        return true;
    }
}
