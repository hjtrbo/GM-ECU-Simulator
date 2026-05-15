using Core.Bus;
using Core.Scheduler;

namespace Core.Ecu.Personas;

// A diagnostic persona is the dispatch table + spec table an ECU presents on
// the wire at a given moment. It is per-ECU, not per-channel: when a kernel
// boot-loads via $36 sub $80 and starts speaking UDS, every tester on the bus
// sees that change. Personas are the cleanest way to keep GMW3110 and UDS
// coexisting without each handler growing protocol-mode flags.
//
// Lifecycle:
//   - Default at EcuNode construction: Gmw3110Persona (the spec the simulator
//     was built around). Real GMW3110 ECUs spend their entire life here.
//   - Service36Handler swaps the active persona to UdsKernelPersona when a
//     $36 sub $80 DownloadAndExecute lands successfully; that handover is the
//     in-spec point at which control passes to the downloaded SPS kernel.
//   - EcuExitLogic resets back to Gmw3110Persona on $20 ReturnToNormalMode
//     or P3C timeout. A once-flashed ECU is a normal ECU again at exit.
//
// What stays out of the persona:
//   - ISO-TP framing (NodeState.Fragmenter/Reassembler) - protocol-agnostic.
//   - SecurityModule - the seed/key algorithm is the algorithm; both personas
//     hand off bytes to the same ISecurityAccessModule.
//   - Channel/bus/scheduler plumbing - the persona is given these by the
//     caller rather than owning them.
public interface IDiagnosticPersona
{
    /// <summary>Stable string id (config/serialization key).</summary>
    string Id { get; }

    /// <summary>Human-readable name for UI / diagnostic logs.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Try to dispatch a USDT request. Returns true if this persona owns the
    /// SID and produced a response (positive, NRC, or intentionally silent).
    /// Returns false if the SID is unknown to this persona - the caller then
    /// emits NRC $11 ServiceNotSupported for physical requests or stays
    /// silent for functional broadcasts.
    /// </summary>
    bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                  bool isFunctional, byte sid, double nowMs, DpidScheduler scheduler);
}
