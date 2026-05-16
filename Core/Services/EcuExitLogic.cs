using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Ecu.Personas;
using Core.Scheduler;
using Core.Transport;

namespace Core.Services;

// Implements GMW3110 §8.5.6.2 Exit_Diagnostic_Services(). Called by:
//   - $20 ReturnToNormalMode (response is requested by tester)
//   - P3C TesterPresent timeout (response is unsolicited)
//
// The node clears all enhanced state but RETAINS dynamic DPID definitions
// per the spec ("the node shall retain all dynamically defined message
// (DPID) information"). $2D-defined dynamic PIDs are cleared since the spec
// doesn't list them as retained.
public static class EcuExitLogic
{
    // The ChannelSession argument may be null when the caller is a P3C
    // timeout - in that case the unsolicited $60 needs to land on whichever
    // channel(s) that ECU was previously talking to. We store the last
    // channel a periodic was scheduled on; if there was no enhanced traffic
    // there's no $20 response to send.
    public static void Run(EcuNode node, DpidScheduler scheduler, ChannelSession? respondOn)
    {
        // Capture before ClearProgrammingState wipes it. Per GMW3110 §8.5.6.2
        // pseudo-code, the $60 positive response is only sent on the
        // `programming_mode_active = NO` branch. §8.5 paragraph "When using
        // this service to end a programming session, ... A valid request for
        // this service which concludes a programming event shall not be
        // followed by a positive response." And §8.5.1 "An ECU shall send an
        // unsolicited service $20 positive response message any time a
        // TesterPresent ($3E) timeout (P3C) occurs and a programming session
        // is not active." Both rules share the same gate.
        bool wasProgrammingActive = node.State.ProgrammingModeActive;

        // GMW3110 §8.16: "An SPS_TYPE_C ECU shall not send a mode $20 response
        // if it receives a mode $20 request message or when a TesterPresent
        // timeout occurs during the phase when the SPS_PrimeReq and
        // SPS_PrimeRsp CAN identifiers are enabled." The "phase enabled" is
        // exactly what DiagnosticResponsesEnabled tracks, so a type-C ECU
        // currently in prime phase suppresses $60 regardless of programming
        // session state.
        bool suppressForSpsTypeC = node.SpsType == Common.Protocol.SpsType.C
                                && node.State.DiagnosticResponsesEnabled;

        // Drop the prime phase before we wipe NormalCommunicationDisabled in
        // ClearProgrammingState - the next $A2 has to re-traverse the $28
        // gate to re-enable responses.
        node.State.DiagnosticResponsesEnabled = false;

        // 1. Reset P3C state.
        node.State.TesterPresent.Deactivate();

        // 2. Reset DPID scheduler for this node (clears Slow/Med/Fast entries).
        scheduler.Stop(node, Array.Empty<byte>());

        // 3. Clear dynamic PIDs from $2D. Static PIDs (the user-configured ones)
        //    stay. We track the dynamic set on the node so this is a clean diff.
        lock (node.State.DynamicallyDefinedPids)
        {
            foreach (var id in node.State.DynamicallyDefinedPids)
                node.RemovePidByAddress(id);
            node.State.DynamicallyDefinedPids.Clear();
        }

        // 3a. Bootloader capture: per-$36 fragments are already on disk
        //     (BootloaderCaptureWriter.WriteEachTransferData fires inline
        //     from Service36Handler). But any flash regions the kernel
        //     declared via $31 EraseMemoryByAddress still need to be
        //     flushed - their $FF-backed buffers got $36 writes mirrored
        //     in but haven't been dumped yet. Functional $20 passes
        //     respondOn=null; fall back to LastEnhancedChannel for the
        //     bus handle. Skip when no channel is reachable (unit-test
        //     paths construct ECUs with no bus attached at all).
        var captureBus = respondOn?.Bus ?? node.State.LastEnhancedChannel?.Bus;
        if (captureBus is not null)
            BootloaderCaptureWriter.WriteFlashRegions(node, captureBus);

        // 3b. Clear $28 / $A5 / $34 / $36 programming-session state. Per
        //     GMW3110 §8.17 "The tester can end a programming event by sending
        //     a ReturnToNormalMode ($20) request message, or by allowing a P3C
        //     timeout to occur." Both paths funnel through here.
        node.State.ClearProgrammingState();

        // 3c. Reset persona back to GMW3110. After a $36 sub $80
        //     DownloadAndExecute the ECU was speaking UDS via UdsKernelPersona;
        //     $20 / P3C timeout is the documented "kernel hands control back to
        //     the boot ROM" point, so the ECU answers as a stock GMW3110 module
        //     again from here on.
        node.Persona = Gmw3110Persona.Instance;

        // 4. Send $60 positive response only when the spec demands it: caller
        //    provided a channel AND a programming session was NOT being torn
        //    down AND the ECU isn't a SPS_TYPE_C in prime phase. Concluding
        //    a programming event is silent on the wire; SPS_TYPE_C in prime
        //    phase is silent per §8.16.
        if (respondOn != null && !wasProgrammingActive && !suppressForSpsTypeC)
        {
            node.State.Fragmenter.EnqueueResponse(respondOn, node.UsdtResponseCanId,
                [Service.Positive(Service.ReturnToNormalMode)]);
        }
    }
}
