using Common.Protocol;
using Core.Bus;
using Core.Scheduler;
using Core.Services;
using Core.Services.Uds;

namespace Core.Ecu.Personas;

// UDS persona presented by a GM SPS programming kernel that has just been
// boot-loaded via $36 sub $80 DownloadAndExecute. Activated by
// Service36Handler when the DownloadAndExecute lands; reset to Gmw3110Persona
// by EcuExitLogic on $20 or P3C timeout.
//
// Scope is deliberately narrow - real kernels only answer a handful of
// services. Anything not listed here falls through to NRC $11
// ServiceNotSupported via the persona's default-false return, which matches
// what powerpcm_flasher and similar tools see on real hardware.
public sealed class UdsKernelPersona : IDiagnosticPersona
{
    public static readonly UdsKernelPersona Instance = new();
    private UdsKernelPersona() { }

    public string Id => "uds-kernel";
    public string DisplayName => "UDS (SPS kernel)";

    public bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                        bool isFunctional, byte sid, double nowMs, DpidScheduler scheduler)
    {
        switch (sid)
        {
            case Iso14229.Service.RoutineControl:
                if (isFunctional) return true;
                if (Service31Handler.Handle(node, usdt, ch))
                    Persona.ActivateP3C(node, ch);
                return true;
            case Service.TesterPresent:
                // ISO 14229 $3E is byte-identical to GMW3110 $3E. The kernel
                // still needs P3C keepalive, so reuse the shared handler.
                Service3EHandler.Handle(node, usdt, ch, isFunctional);
                return true;
            case Service.ReturnToNormalMode:
                // $20 is the documented way for the tester to ask the kernel
                // to hand control back to the boot ROM. EcuExitLogic resets
                // the persona to GMW3110 as part of its cleanup.
                if (isFunctional) { EcuExitLogic.Run(node, scheduler, null); return true; }
                Service20Handler.Handle(node, usdt, ch, scheduler);
                return true;
            case Service.RequestDownload:
                // Some kernels accept a second $34/$36 pair to layer in
                // calibration after the OS upload. Forward to the same
                // handler the GMW3110 persona uses - the wire shape is
                // compatible for the cases SPS kernels send.
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
