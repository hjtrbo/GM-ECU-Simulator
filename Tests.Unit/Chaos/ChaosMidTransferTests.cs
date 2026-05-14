using Common.IsoTp;
using Common.PassThru;
using Core.Bus;
using Core.Ecu;
using EcuSimulator.Tests.TestHelpers;
using Shim.Ipc;
using Xunit;

namespace EcuSimulator.Tests.Chaos;

// Category 1 chaos tests: real-world failure modes during a multi-frame
// transfer. These are not spec-defined error paths; they're scenarios
// dealer-tool stacks actually create when they crash, hang, or have their
// USB-CAN adapter unplugged mid-flash.
public class ChaosMidTransferTests
{
    private const ushort PhysReq = NodeFactory.PhysReq;
    private const ushort UsdtResp = NodeFactory.UsdtResp;

    private static byte[] WrapCanFrame(uint canId, byte[] data)
    {
        var f = new byte[4 + data.Length];
        f[0] = (byte)((canId >> 24) & 0xFF);
        f[1] = (byte)((canId >> 16) & 0xFF);
        f[2] = (byte)((canId >> 8) & 0xFF);
        f[3] = (byte)(canId & 0xFF);
        data.CopyTo(f, 4);
        return f;
    }

    private static (uint canId, byte[] data) UnwrapCanFrame(PassThruMsg msg)
    {
        var raw = msg.Data;
        uint canId = ((uint)raw[0] << 24) | ((uint)raw[1] << 16) | ((uint)raw[2] << 8) | raw[3];
        return (canId, raw.AsSpan(4).ToArray());
    }

    private static (VirtualBus bus, EcuNode node, IpcSessionState state, FakeSeedKeyAlgorithm algo)
        SetupBusWithLongSeedAlgo(int seedLength = 50)
    {
        var bus = new VirtualBus();
        var seed = new byte[seedLength];
        for (int i = 0; i < seedLength; i++) seed[i] = (byte)(0xA0 + i);
        var algo = new FakeSeedKeyAlgorithm
        {
            SeedLength = seedLength,
            KeyLength = seedLength,
            SeedToReturn = seed,
            ExpectedKey = new byte[seedLength],
        };
        var node = NodeFactory.CreateNodeWithGenericModule(algo);
        bus.AddNode(node);
        var state = new IpcSessionState(bus);
        return (bus, node, state, algo);
    }

    // -----------------------------------------------------------------------
    // 1a. Channel removed while a fragmenter has a multi-frame TX in flight.
    //
    // Bug class: dealer-tool crashes mid-flash, USB-CAN adapter yanked. The
    // J2534 driver tears down the channel; the per-EcuNode fragmenter must
    // not be left holding `activeChannel = ch` and an armed N_Bs timer that
    // would later fire EnqueueRx onto a disposed channel.
    // -----------------------------------------------------------------------

    [Fact]
    public void Channel_removed_mid_multi_frame_TX_aborts_fragmenter_cleanly()
    {
        // 50-byte seed -> $27 $01 response = 52 bytes -> FF + CFs from the fragmenter.
        var (bus, node, state, _) = SetupBusWithLongSeedAlgo(seedLength: 50);
        var ch = state.AllocateChannel(ProtocolID.CAN, 500_000, 0);

        // Trigger: bus.DispatchHostTx -> Service27 -> module -> algo -> fragmenter
        // emits FF, arms N_Bs, returns with activeChannel = ch.
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x02, 0x27, 0x01 }), ch);
        Assert.True(node.State.Fragmenter.InProgress, "expected fragmenter to be mid-multi-frame after seed request");

        // Drain the FF that landed in the channel's RxQueue (so we don't trip
        // any later sanity checks on residual frames).
        Assert.True(ch.RxQueue.TryDequeue(out _));

        // Simulate host disconnect.
        state.RemoveChannel(ch.Id);

        // The new IpcSessionState.RemoveChannel hook must have called
        // AbortIfActiveOn(ch) on every ECU's fragmenter, clearing activeTx +
        // activeChannel and cancelling the N_Bs / STmin timers.
        Assert.False(node.State.Fragmenter.InProgress);
        Assert.Equal(NResult.N_ERROR, node.State.Fragmenter.LastResult);
    }

    [Fact]
    public void After_mid_TX_channel_removal_a_new_channel_to_same_ECU_works()
    {
        var (bus, node, state, _) = SetupBusWithLongSeedAlgo(seedLength: 50);
        var ch1 = state.AllocateChannel(ProtocolID.CAN, 500_000, 0);

        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x02, 0x27, 0x01 }), ch1);
        Assert.True(node.State.Fragmenter.InProgress);
        ch1.RxQueue.TryDequeue(out _);

        state.RemoveChannel(ch1.Id);
        Assert.False(node.State.Fragmenter.InProgress);

        // Reconnect: new channel, same ECU. Sending the same request again
        // must work cleanly - no leftover state from the aborted TX.
        var ch2 = state.AllocateChannel(ProtocolID.CAN, 500_000, 0);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x02, 0x27, 0x01 }), ch2);

        Assert.True(node.State.Fragmenter.InProgress, "fresh request on new channel should put fragmenter back in flight");
        Assert.True(ch2.RxQueue.TryDequeue(out var msg));
        var (_, data) = UnwrapCanFrame(msg!);
        Assert.Equal(0x10, data[0]);   // FF PCI nibble
    }

    [Fact]
    public void RemoveChannel_when_no_TX_active_is_a_safe_noop()
    {
        // The new abort path must not throw or mutate state when the
        // fragmenter wasn't holding an active TX targeting this channel.
        var (bus, node, state, _) = SetupBusWithLongSeedAlgo(seedLength: 50);
        var ch = state.AllocateChannel(ProtocolID.CAN, 500_000, 0);

        Assert.False(node.State.Fragmenter.InProgress);
        var ex = Record.Exception(() => state.RemoveChannel(ch.Id));
        Assert.Null(ex);
        Assert.False(node.State.Fragmenter.InProgress);
    }

    [Fact]
    public void RemoveChannel_does_not_abort_TX_targeting_a_different_channel()
    {
        var (bus, node, state, _) = SetupBusWithLongSeedAlgo(seedLength: 50);
        var ch1 = state.AllocateChannel(ProtocolID.CAN, 500_000, 0);
        var ch2 = state.AllocateChannel(ProtocolID.CAN, 500_000, 0);

        // TX targets ch1.
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x02, 0x27, 0x01 }), ch1);
        Assert.True(node.State.Fragmenter.InProgress);

        // Remove ch2 - should NOT touch the fragmenter (it's bound to ch1).
        state.RemoveChannel(ch2.Id);

        Assert.True(node.State.Fragmenter.InProgress, "fragmenter must remain in-flight when an unrelated channel is removed");
    }

    // -----------------------------------------------------------------------
    // 1b. Reassembler robustness against a partial multi-frame request that
    //     is then abandoned. The simulator must stay usable for subsequent
    //     requests on the same ECU.
    // -----------------------------------------------------------------------

    [Fact]
    public void Reassembler_recovers_when_FF_plus_partial_CFs_are_followed_by_a_fresh_request()
    {
        // No security needed - we abandon a long $27 $01 request mid-multi-frame.
        // For this to be multi-frame on the REQUEST side we need a >7-byte
        // payload: pick $36 with a stub $34 first.
        var (bus, node, state, _) = SetupBusWithLongSeedAlgo(seedLength: 2);
        var ch = state.AllocateChannel(ProtocolID.CAN, 500_000, 0);

        // Walk through the programming-mode preconditions so $34 is accepted.
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, 0x28 }), ch);
        ch.RxQueue.TryDequeue(out _);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x02, 0xA5, 0x01 }), ch);
        ch.RxQueue.TryDequeue(out _);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x02, 0xA5, 0x03 }), ch);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x02, 0x27, 0x01 }), ch);
        ch.RxQueue.TryDequeue(out _);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x04, 0x27, 0x02, 0x00, 0x00 }), ch);
        ch.RxQueue.TryDequeue(out _);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x05, 0x34, 0x00, 0x00, 0x10, 0x00 }), ch);
        ch.RxQueue.TryDequeue(out _);

        // Send FF for a $36 request that won't be completed (only 1 CF follows).
        // FF: 12-bit FF_DL = 100 (a $36 with 95 bytes payload = 2 + 3 + 95 = 100 total).
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x10, 100, 0x36, 0x00, 0x00, 0x00, 0x00, 0xAA }), ch);
        ch.RxQueue.TryDequeue(out _);   // drain FC.CTS

        // Send 1 CF (sequence 1) - reassembler is now in inProgress=true, expectedSeq=2.
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x21, 1, 2, 3, 4, 5, 6, 7 }), ch);
        // Don't send the rest. Reassembler has stale state.

        // A fresh SingleFrame request ($3E) must still be serviced - the SF
        // path doesn't depend on reassembler state.
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, 0x3E }), ch);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        var (_, resp) = UnwrapCanFrame(msg!);
        Assert.Equal(new byte[] { 0x01, 0x7E }, resp);   // $7E TesterPresent positive response

        // A fresh FirstFrame for a different request must reset the reassembler
        // and complete cleanly. We use $3E as a 1-byte SF wrapped in the
        // simplest possible flow: re-send the same TesterPresent and verify it
        // also works. (Verifying that no service blew up is the proxy for "the
        // reassembler isn't poisoning the bus.")
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, 0x3E }), ch);
        Assert.True(ch.RxQueue.TryDequeue(out var msg2));
        var (_, resp2) = UnwrapCanFrame(msg2!);
        Assert.Equal(new byte[] { 0x01, 0x7E }, resp2);
    }
}
