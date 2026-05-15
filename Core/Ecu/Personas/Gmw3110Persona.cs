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
        switch (sid)
        {
            case Service.ReadDataByIdentifier:
                if (isFunctional) return true;
                Service1AHandler.Handle(node, usdt, ch);
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
                if (isFunctional) return true;
                if (ServiceA2Handler.Handle(node, usdt, ch))
                    Persona.ActivateP3C(node, ch);
                return true;
            case Service.ProgrammingMode:
                if (isFunctional) return true;
                if (ServiceA5Handler.Handle(node, usdt, ch))
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
            default:
                return false;
        }
    }
}
