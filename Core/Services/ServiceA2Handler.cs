using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// $A2 ReportProgrammedState. GMW3110-2010 §8.16 (p179-181).
//
// Request: SID $A2 only. No sub-function, no data parameters (§8.16.2.1/2).
// Positive response: $E2 + programmedState (1 byte) per §8.16.3 Table 160.
//
// programmedState values (Table 160):
//   $00 FP   fully programmed
//   $01 NSC  no op s/w or cal data
//   $02 NC   op s/w present, cal missing
//   $03 SDC  s/w present, default/no-start cal
//   $50 GMF  general memory fault
//   $51 RMF  RAM memory fault
//   $52 NVRMF NVRAM memory fault
//   $53 BMF  boot memory failure
//   $54 FMF  flash memory failure
//   $55 EEMF EEPROM memory failure
//
// NRCs (§8.16.4 Table 162):
//   $12 SFNS-IF   request has more bytes than the SID
//   $78 RCR-RP    programmedState calc not yet complete (we don't simulate this)
//
// The state byte itself is configured on EcuNode.ProgrammedState (default 0x00),
// so users can simulate a partially-programmed or fault state when testing
// programming-tool error paths.
public static class ServiceA2Handler
{
    /// <summary>
    /// Returns true on positive response (so the dispatcher can refresh P3C).
    /// $A2 IS a diagnostic-session-extending service in the spec's eyes - it's
    /// used during programming setup, so it counts as enhanced traffic.
    /// </summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        if (usdtPayload.Length != 1 || usdtPayload[0] != Service.ReportProgrammedState)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.ReportProgrammedState, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(Service.ReportProgrammedState), node.ProgrammedState]);
        return true;
    }
}
