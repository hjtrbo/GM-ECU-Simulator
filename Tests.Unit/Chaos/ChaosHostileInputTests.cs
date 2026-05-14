using Common.PassThru;
using Core.Bus;
using Core.Ecu;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Chaos;

// Category 2 chaos tests: ill-formed or hostile inputs from the host. The
// simulator must reject these with the appropriate NRC and stay usable for
// well-formed traffic that follows.
public class ChaosHostileInputTests
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

    private static byte[] UnwrapPayload(PassThruMsg msg)
    {
        var raw = msg.Data;
        // For these tests every response is an SF, so PCI byte is at offset 4.
        int len = raw[4] & 0x0F;
        return raw.AsSpan(5, len).ToArray();
    }

    private static (VirtualBus bus, EcuNode node, ChannelSession ch) SetupBus()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNodeWithGenericModule();
        // 3-byte $36 starting addresses are baked into the test payloads.
        node.DownloadAddressByteCount = 3;
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        return (bus, node, ch);
    }

    private static void GetIntoUnlockedProgrammingMode(VirtualBus bus, ChannelSession ch)
    {
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, 0x28 }), ch);
        ch.RxQueue.TryDequeue(out _);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x02, 0xA5, 0x01 }), ch);
        ch.RxQueue.TryDequeue(out _);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x02, 0xA5, 0x03 }), ch);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x02, 0x27, 0x01 }), ch);
        ch.RxQueue.TryDequeue(out _);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x04, 0x27, 0x02, 0xAB, 0xCD }), ch);
        ch.RxQueue.TryDequeue(out _);
    }

    // -----------------------------------------------------------------------
    // 2a. $34 with declaredSize > MaxDownloadBufferBytes must be rejected
    //     before any allocation, and the channel must remain usable.
    // -----------------------------------------------------------------------

    [Fact]
    public void Service34_with_huge_unCompressedMemorySize_returns_NRC_22_and_does_not_allocate()
    {
        var (bus, node, ch) = SetupBus();
        GetIntoUnlockedProgrammingMode(bus, ch);

        // Declare 1 GiB - well above the 16 MiB cap. 4-byte size = $40 $00 $00 $00.
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] {
            0x06, 0x34, 0x00, 0x40, 0x00, 0x00, 0x00 }), ch);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(new byte[] { 0x7F, 0x34, 0x22 }, UnwrapPayload(msg!));

        // No buffer was allocated (the cap fires BEFORE the allocation).
        Assert.Null(node.State.DownloadBuffer);
        Assert.False(node.State.DownloadActive);

        // A subsequent $34 with a sane size must still work.
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] {
            0x05, 0x34, 0x00, 0x00, 0x10, 0x00 }), ch);
        Assert.True(ch.RxQueue.TryDequeue(out var ok));
        Assert.Equal(new byte[] { 0x74 }, UnwrapPayload(ok!));
        Assert.True(node.State.DownloadActive);
        Assert.Equal(4096, node.State.DownloadBuffer!.Length);
    }

    [Fact]
    public void Service34_at_exact_max_buffer_size_succeeds()
    {
        var (bus, node, ch) = SetupBus();
        GetIntoUnlockedProgrammingMode(bus, ch);

        // 16 MiB exactly = 0x01000000.
        uint exact = Service34Handler.MaxDownloadBufferBytes;
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] {
            0x06, 0x34, 0x00,
            (byte)((exact >> 24) & 0xFF),
            (byte)((exact >> 16) & 0xFF),
            (byte)((exact >> 8)  & 0xFF),
            (byte)(exact & 0xFF) }), ch);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(new byte[] { 0x74 }, UnwrapPayload(msg!));
        Assert.Equal((int)exact, node.State.DownloadBuffer!.Length);
    }

    // -----------------------------------------------------------------------
    // 2b. $36 startingAddress + dataRecord must not silently overflow into a
    //     buffer-bounds bypass. Pre-existing test covers offset-past-end; this
    //     one tests near-uint.MaxValue arithmetic.
    // -----------------------------------------------------------------------

    [Fact]
    public void Service36_startingAddress_near_uint_max_returns_NRC_31()
    {
        var (bus, node, ch) = SetupBus();
        GetIntoUnlockedProgrammingMode(bus, ch);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] {
            0x05, 0x34, 0x00, 0x00, 0x10, 0x00 }), ch);    // 4 KiB buffer
        ch.RxQueue.TryDequeue(out _);

        // $36 sub $00, 3-byte startingAddress = $FF $FF $FE (16 MiB - 2),
        // then 4 data bytes. uint addr 16777214; addr + 4 = 16777218 - way
        // past the 4096-byte buffer.
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] {
            0x09, 0x36, 0x00, 0xFF, 0xFF, 0xFE, 0xAA, 0xBB, 0xCC, 0xDD }), ch);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(new byte[] { 0x7F, 0x36, 0x31 }, UnwrapPayload(msg!));

        // Buffer must be untouched.
        Assert.All(node.State.DownloadBuffer!, b => Assert.Equal((byte)0, b));
    }

    // -----------------------------------------------------------------------
    // 2c. Reassembler must not crash on malformed PCI in CF position.
    // -----------------------------------------------------------------------

    [Fact]
    public void Reassembler_ignores_malformed_PCI_in_CF_position()
    {
        var (bus, _, ch) = SetupBus();
        GetIntoUnlockedProgrammingMode(bus, ch);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] {
            0x05, 0x34, 0x00, 0x00, 0x10, 0x00 }), ch);
        ch.RxQueue.TryDequeue(out _);

        // FF for a 100-byte $36 request (FF_DL = 100, header $36 $00 + 3-byte addr + 95 bytes).
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] {
            0x10, 100, 0x36, 0x00, 0x00, 0x00, 0x00, 0xAA }), ch);
        ch.RxQueue.TryDequeue(out _);   // FC.CTS

        // Reserved PCI nibble (0xF0) where a CF should be. Existing
        // reassembler falls into `default: return null` - no exception, no
        // state corruption.
        var ex = Record.Exception(() =>
            bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0xF0, 1, 2, 3, 4, 5, 6, 7 }), ch));
        Assert.Null(ex);

        // The simulator must still respond to a fresh SF request afterwards.
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, 0x3E }), ch);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(new byte[] { 0x7E }, UnwrapPayload(msg!));
    }

    // -----------------------------------------------------------------------
    // 2d. Wrong-SN CF resets the reassembler; subsequent FF is accepted.
    // -----------------------------------------------------------------------

    [Fact]
    public void Wrong_SN_in_CF_resets_reassembler_then_fresh_FF_works()
    {
        var (bus, _, ch) = SetupBus();
        GetIntoUnlockedProgrammingMode(bus, ch);
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] {
            0x05, 0x34, 0x00, 0x00, 0x10, 0x00 }), ch);
        ch.RxQueue.TryDequeue(out _);

        // FF with FF_DL = 30 ($36 + sub + 3-byte addr + 25 data = 30 total).
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] {
            0x10, 30, 0x36, 0x00, 0x00, 0x00, 0x00, 0x01 }), ch);
        ch.RxQueue.TryDequeue(out _);   // FC.CTS

        // Send CF with wrong SN ($25 instead of $21) - reassembler resets per §9.6.4.4.
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x25, 2, 3, 4, 5, 6, 7, 8 }), ch);

        // Now send a fresh, complete SF request - must work.
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, 0x3E }), ch);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(new byte[] { 0x7E }, UnwrapPayload(msg!));
    }
}
