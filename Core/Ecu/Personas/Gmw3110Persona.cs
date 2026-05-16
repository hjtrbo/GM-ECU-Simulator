using Common.Protocol;
using Core.Bus;
using Core.Scheduler;
using Core.Services;

namespace Core.Ecu.Personas;

// GMW3110-2010 persona: the default dispatch table every EcuNode starts with.
// Body is the verbatim case-by-case dispatch that used to live inline in
// VirtualBus.DispatchUsdtBody - the move is mechanical, not semantic.
//
// What is INTENTIONALLY missing from this table:
//   - $31 RoutineControl. Not a GMW3110 service. Lives in UdsKernelPersona,
//     active only after $36 sub $80 DownloadAndExecute hands the bus to the
//     SPS kernel. A tester that sends $31 to a baseline GMW3110 ECU now gets
//     the spec-correct NRC $11 ServiceNotSupported (via the persona's
//     default-false return), where the old inline dispatcher answered with
//     a fake-positive after $27 unlock.
public sealed class Gmw3110Persona : IDiagnosticPersona
{
    public static readonly Gmw3110Persona Instance = new();
    private Gmw3110Persona() { }

    public string Id => "gmw3110";
    public string DisplayName => "GMW3110-2010";

    public bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                        bool isFunctional, byte sid, double nowMs, DpidScheduler scheduler)
    {
        // §8.16 SPS_TYPE_C activation gate. A blank/unprogrammed ECU is silent
        // on every request until it receives $A2 while $28 DisableNormal-
        // Communication is active; that pair flips DiagnosticResponsesEnabled
        // and from then on the ECU behaves like SPS_TYPE_A (handled by the
        // switch below). Two state-changing requests are observed while silent:
        //   - $28 functional: track NormalCommunicationDisabled, no response
        //   - $A2 (with $28 active): activate, respond on UsdtResponseCanId
        //     (which the user configures = SPS_PrimeRsp $300|addr)
        // Everything else is silently dropped per §8.16 ("shall not respond to
        // any diagnostic request until diagnostic responses are enabled").
        if (node.SpsType == Common.Protocol.SpsType.C && !node.State.DiagnosticResponsesEnabled)
        {
            return DispatchSpsTypeCSilent(node, usdt, ch, isFunctional, sid);
        }

        switch (sid)
        {
            case Service.ReadDataByIdentifier:
                // §8.3 + DPS PM p.241: $1A $B0 functional is the canonical
                // "who's on the bus" probe; each ECU answers physically with
                // "5A B0 <diag_addr>". Dispatch both addressing modes - the
                // handler suppresses NRCs on functional for non-$B0 DIDs to
                // avoid bus storms (mirrors $A2 / $A5 policy).
                Service1AHandler.Handle(node, usdt, ch, isFunctional);
                return true;
            case Service.ReadDataByParameterIdentifier:
                // §8.6 explicitly supports functional addressing (see Tables 87
                // and 89 in the spec): each ECU responds with the PIDs it
                // supports; ECUs that don't support any of the requested PIDs
                // stay silent. The handler enforces the silent-on-functional
                // rule when no PIDs match.
                Service22Handler.Handle(node, usdt, ch, nowMs, isFunctional);
                return true;
            case Service.DefinePidByAddress:
                if (isFunctional) return true;
                if (Service2DHandler.Handle(node, usdt, ch))
                    Persona.ActivateP3C(node, ch);
                return true;
            case Service.DynamicallyDefineMessage:
                if (isFunctional) return true;
                if (Service2CHandler.Handle(node, usdt, ch))
                    Persona.ActivateP3C(node, ch);
                return true;
            case Service.ReadDataByPacketIdentifier:
                if (isFunctional) return true;
                if (ServiceAAHandler.Handle(node, usdt, ch, scheduler))
                    Persona.ActivateP3C(node, ch);
                return true;
            case Service.TesterPresent:
                Service3EHandler.Handle(node, usdt, ch, isFunctional);
                return true;
            case Service.ReturnToNormalMode:
                if (isFunctional) { EcuExitLogic.Run(node, scheduler, null); return true; }
                Service20Handler.Handle(node, usdt, ch, scheduler);
                return true;
            case Service.InitiateDiagnosticOperation:
                // §8.2.5.1 (p. 79): the canonical disableAllDTCs flow is a
                // functional broadcast on $101/$FE with every responding node
                // sending $50 on its USDT response ID. The §8.2.6.2 pseudo-code
                // is addressing-agnostic. 6Speed.T43 kernelprep() relies on
                // this and prints "101 10 02 command failed" when the
                // functional reply doesn't arrive within 100 ms.
                if (Service10Handler.Handle(node, usdt, ch))
                    Persona.ActivateP3C(node, ch);
                return true;
            case Service.SecurityAccess:
                if (isFunctional) return true;
                if (Service27Handler.Handle(node, usdt, ch, (long)nowMs))
                    Persona.ActivateP3C(node, ch);
                return true;
            case Service.DisableNormalCommunication:
                // §8.9: typically functional broadcast at $101 / $FE; both
                // physical and functional are accepted per §8.9.5.1.
                if (Service28Handler.Handle(node, usdt, ch, isFunctional))
                    Persona.ActivateP3C(node, ch);
                return true;
            case Service.ReportProgrammedState:
                // §8.16: $A2 functional is the spec mechanism for enumerating
                // programmable ECUs on the bus. GM SPS / DPS broadcasts $A2 on
                // $101/$FE and counts each $E2 reply to populate its mapping
                // matrix. Dispatch the handler for both physical and functional.
                if (ServiceA2Handler.Handle(node, usdt, ch, isFunctional))
                    Persona.ActivateP3C(node, ch);
                return true;
            case Service.ProgrammingMode:
                // §8.17.5.1 Table 169: $A5 $01/$02/$03 are sent on functional
                // $101/$FE; each programmable node responds on its physical
                // response ID. Dispatch in both addressing modes (parity with
                // $A2 enumeration above). The DPS PM page 241 wire trace
                // matches this exactly.
                if (ServiceA5Handler.Handle(node, usdt, ch, isFunctional))
                    Persona.ActivateP3C(node, ch);
                return true;
            case Service.RequestDownload:
                if (isFunctional) return true;
                if (Service34Handler.Handle(node, usdt, ch))
                    Persona.ActivateP3C(node, ch);
                return true;
            case Service.TransferData:
                if (isFunctional) return true;
                if (Service36Handler.Handle(node, usdt, ch))
                    Persona.ActivateP3C(node, ch);
                return true;
            case Service.WriteDataByIdentifier:
                // §8.14 - point-to-point only in the worked example (§8.14.5.1
                // uses physical request $241). The pseudo code in §8.14.6.2
                // doesn't gate on addressing; a functional broadcast write to
                // 17-byte VIN doesn't make sense anyway (every ECU would write
                // the same VIN to its own slot), so we silently drop functional.
                if (isFunctional) return true;
                if (Service3BHandler.Handle(node, usdt, ch))
                    Persona.ActivateP3C(node, ch);
                return true;
            default:
                return false;
        }
    }

    // SPS_TYPE_C pre-activation handler. Updates the internal $28 state without
    // emitting a response; activates the ECU on $A2 + $28; drops everything
    // else silently. The bus already requires DiagnosticResponsesEnabled before
    // sending a frame on the wire (LogRx skipping etc. is unaffected - the
    // silence is achieved by NOT calling Fragmenter.EnqueueResponse at all).
    private static bool DispatchSpsTypeCSilent(EcuNode node, ReadOnlySpan<byte> usdt,
                                                ChannelSession ch, bool isFunctional, byte sid)
    {
        switch (sid)
        {
            case Service.DisableNormalCommunication:
                // Track state without responding. The full handler also emits a
                // positive reply; we just want the state flip.
                if (usdt.Length == 1) node.State.NormalCommunicationDisabled = true;
                return true;
            case Service.ReportProgrammedState:
                // Activation gate. Per §8.16 the ECU enables diagnostic responses
                // on receipt of $A2 while $28 active, then this $A2 itself is
                // answered on SPS_PrimeRsp. Malformed $A2 stays silent (carpet-
                // bomb NRC would defeat the purpose of being a quiet blank ECU).
                if (usdt.Length != 1) return true;
                if (!node.State.NormalCommunicationDisabled) return true;
                node.State.DiagnosticResponsesEnabled = true;
                ServiceA2Handler.Handle(node, usdt, ch, isFunctional);
                Persona.ActivateP3C(node, ch);
                return true;
            case Service.ReadDataByIdentifier:
                // Second activation gate: $1A $B0 (Read DID = ECU Diagnostic
                // Address) is the canonical post-reset re-discovery probe DPS
                // sends after a successful flash. Per DPS PM page 241 every
                // ECU answers it on its physical USDT response ID with
                // "5A B0 <diag_addr>". We accept it from the silent state too:
                // a real ECU just rebooted out of programming mode has lost
                // its activation flag but still answers this query so the
                // tester can rebuild its mapping matrix. Other DIDs stay
                // silent in this pre-activation state.
                if (usdt.Length != 2 || usdt[1] != Service1AHandler.DidEcuDiagnosticAddress)
                    return true;
                node.State.DiagnosticResponsesEnabled = true;
                Service1AHandler.Handle(node, usdt, ch, isFunctional);
                Persona.ActivateP3C(node, ch);
                return true;
            default:
                // Per §8.16 "shall not respond to any diagnostic request until
                // diagnostic responses are enabled" - silent drop.
                return true;
        }
    }
}
