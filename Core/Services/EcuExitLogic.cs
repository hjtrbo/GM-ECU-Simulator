using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
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
    // timeout — in that case the unsolicited $60 needs to land on whichever
    // channel(s) that ECU was previously talking to. We store the last
    // channel a periodic was scheduled on; if there was no enhanced traffic
    // there's no $20 response to send.
    public static void Run(EcuNode node, DpidScheduler scheduler, ChannelSession? respondOn)
    {
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

        // 3a. Bootloader capture: if the user has the Capture Bootloader tab's
        //     checkbox on and a $36 payload was assembled during this session,
        //     dump it to disk BEFORE ClearProgrammingState wipes the buffer.
        //     No-op when capture is off (the spec-correct default).
        BootloaderCaptureWriter.MaybeWrite(node, scheduler.Bus);

        // 3b. Clear $28 / $A5 / $34 / $36 programming-session state. Per
        //     GMW3110 §8.17 "The tester can end a programming event by sending
        //     a ReturnToNormalMode ($20) request message, or by allowing a P3C
        //     timeout to occur." Both paths funnel through here.
        node.State.ClearProgrammingState();

        // 4. Send unsolicited $60 positive response (only if we have a channel).
        if (respondOn != null)
        {
            node.State.Fragmenter.EnqueueResponse(respondOn, node.UsdtResponseCanId,
                [Service.Positive(Service.ReturnToNormalMode)]);
        }
    }
}
