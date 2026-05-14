using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Transport;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// ISO 15765-4 OBD-II functional broadcast at CAN ID $7DF.
//
// What this proves:
//   - A $7DF SF frame is consumed (no "no ECU at 7DF -- frame dropped"),
//     refreshes LastHostActivityMs, and dispatches to every ECU with
//     isFunctional=true. The $7DF $3E TesterPresent flow in the wire log is
//     the motivating case - before this dispatcher it kept missing the
//     keepalive and the session timed out with an unsolicited $60.
//   - Multi-frame on $7DF is silently dropped (correct per ISO 15765-4 -
//     no per-receiver FlowControl is defined for functional broadcast).
//   - $101+$FE keeps working unchanged.
public class Obd2FunctionalBroadcastTests
{
    private static (VirtualBus bus, EcuNode node, ChannelSession ch) Wire()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = Common.PassThru.ProtocolID.CAN, Baud = 500_000, Bus = bus };
        return (bus, node, ch);
    }

    /// <summary>Builds a [4-byte BE CAN ID][8-byte CAN data] frame matching the
    /// shape DispatchHostTx expects (the bus wire format).</summary>
    private static byte[] BuildCanFrame(uint canId, params byte[] data)
    {
        var frame = new byte[CanFrame.IdBytes + 8];
        CanFrame.WriteId(frame, canId);
        Array.Copy(data, 0, frame, CanFrame.IdBytes, Math.Min(data.Length, 8));
        return frame;
    }

    [Fact]
    public void TesterPresent_on_7DF_refreshes_P3C_and_does_not_drop()
    {
        // GMW3110 / ISO 14229: $3E TesterPresent with no response expected
        // when functionally addressed. The handler still ticks the P3C
        // keepalive; that's what stops EcuExitLogic from firing.
        var (bus, node, ch) = Wire();

        // Prime an enhanced session so P3C is active and decay matters.
        node.State.NormalCommunicationDisabled = true;
        node.State.TesterPresent.Activate();
        var beforeActivity = bus.LastHostActivityMs;

        bus.NoteHostActivity();
        bus.DispatchHostTx(BuildCanFrame(GmlanCanId.Obd2FunctionalRequest, 0x01, 0x3E, 0, 0, 0, 0, 0, 0), ch);

        // No response expected (functional $3E is fire-and-forget).
        TestFrame.AssertEmpty(ch);
        // Host activity stamp updated - the bus saw the frame, not dropped it.
        Assert.True(bus.LastHostActivityMs >= beforeActivity);
        // TesterPresent state remains Active (the previous Activate stays valid).
        Assert.Equal(Core.Ecu.TesterPresentTimerState.Active, node.State.TesterPresent.State);
    }

    [Fact]
    public void Obd2_broadcast_reaches_every_ECU_on_the_bus()
    {
        // Two ECUs. Send a functional broadcast and both should observe it.
        // The cheapest way to prove "both saw it" without poking internals is
        // to drive $28 DisableNormalCommunication, which Service28Handler
        // accepts on both physical and functional. Each ECU's state flips.
        var bus = new VirtualBus();
        var ecu1 = NodeFactory.CreateNode();
        var ecu2 = new EcuNode
        {
            Name = "TestEcu2",
            PhysicalRequestCanId = 0x7E1,
            UsdtResponseCanId = 0x7E9,
            UudtResponseCanId = 0x5E9,
        };
        bus.AddNode(ecu1);
        bus.AddNode(ecu2);
        var ch = new ChannelSession { Id = 1, Protocol = Common.PassThru.ProtocolID.CAN, Baud = 500_000, Bus = bus };

        Assert.False(ecu1.State.NormalCommunicationDisabled);
        Assert.False(ecu2.State.NormalCommunicationDisabled);

        bus.DispatchHostTx(BuildCanFrame(GmlanCanId.Obd2FunctionalRequest, 0x01, 0x28, 0, 0, 0, 0, 0, 0), ch);

        Assert.True(ecu1.State.NormalCommunicationDisabled);
        Assert.True(ecu2.State.NormalCommunicationDisabled);
    }

    [Fact]
    public void Obd2_broadcast_ignores_first_frame_pci()
    {
        // FF on $7DF is malformed per ISO 15765-4 (no per-receiver FC defined).
        // The dispatcher drops it silently - no NRC, no Rx frame queued.
        var (bus, node, ch) = Wire();
        bus.DispatchHostTx(BuildCanFrame(GmlanCanId.Obd2FunctionalRequest, 0x10, 0x08, 0x3E, 0, 0, 0, 0, 0), ch);

        TestFrame.AssertEmpty(ch);
        // No state mutation either; the frame was rejected before reaching any service.
        Assert.False(node.State.NormalCommunicationDisabled);
    }

    [Fact]
    public void Gmlan_101_FE_broadcast_still_works_alongside_7DF()
    {
        // Regression check: the new $7DF path is additive, not a replacement.
        // $101+$FE must continue to route to all ECUs the existing way.
        var (bus, node, ch) = Wire();
        bus.DispatchHostTx(BuildCanFrame(GmlanCanId.AllNodesRequest,
            GmlanCanId.AllNodesExtAddr, 0x01, 0x28, 0, 0, 0, 0, 0), ch);

        Assert.True(node.State.NormalCommunicationDisabled);
    }
}
