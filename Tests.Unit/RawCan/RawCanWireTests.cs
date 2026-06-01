using Core.Transport;
using Shim.Ipc;
using Xunit;

namespace EcuSimulator.Tests.RawCan;

// RawCanWire is the 13-byte fixed framing for one CAN frame on the raw-CAN TCP
// link to the gauge sim. These are pure codec tests - no socket. The wire ID is
// big-endian and must match CanFrame's internal layout byte-for-byte so the
// bridge is a straight copy.
public sealed class RawCanWireTests
{
    [Theory]
    [InlineData(0x7E0u, new byte[] { 0x02, 0x01, 0x0C })]      // 11-bit, OBD-II request
    [InlineData(0x7E8u, new byte[] { 0x06, 0x41, 0x0C, 0x0B, 0xB8, 0x00, 0x00 })]
    [InlineData(0x5E8u, new byte[] { 0xFE, 0x12 })]            // UUDT-style
    [InlineData(0x18DAF110u, new byte[] { 0x03, 0x22, 0x12, 0x34 })] // 29-bit extended
    public void RoundTrips_11_and_29_bit_ids_and_various_dlc(uint canId, byte[] data)
    {
        // internal = [4-byte BE id][data]
        var internalIn = new byte[CanFrame.IdBytes + data.Length];
        CanFrame.WriteId(internalIn, canId);
        data.CopyTo(internalIn, CanFrame.IdBytes);

        Span<byte> wire = stackalloc byte[RawCanWire.FrameSize];
        RawCanWire.FromInternal(internalIn, wire);

        // DLC + 29-bit flag encoded in byte 0.
        Assert.Equal(data.Length, wire[0] & 0x0F);
        bool expectedExtended = canId > 0x7FF;
        Assert.Equal(expectedExtended, (wire[0] & 0x80) != 0);

        // ID round-trips through the wire as big-endian.
        var internalOut = RawCanWire.ToInternal(wire);
        Assert.Equal(canId, CanFrame.ReadId(internalOut));
        Assert.Equal(data, CanFrame.Payload(internalOut).ToArray());
        Assert.Equal(internalIn, internalOut);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    public void Handles_every_classical_dlc_including_zero_and_eight(int dlc)
    {
        var data = new byte[dlc];
        for (int i = 0; i < dlc; i++) data[i] = (byte)(0x10 + i);
        var internalIn = new byte[CanFrame.IdBytes + dlc];
        CanFrame.WriteId(internalIn, 0x7E0);
        data.CopyTo(internalIn, CanFrame.IdBytes);

        Span<byte> wire = stackalloc byte[RawCanWire.FrameSize];
        RawCanWire.FromInternal(internalIn, wire);

        Assert.Equal(dlc, wire[0] & 0x0F);
        var internalOut = RawCanWire.ToInternal(wire);
        Assert.Equal(data, CanFrame.Payload(internalOut).ToArray());
    }

    [Fact]
    public void FromInternal_zero_pads_the_unused_data_tail()
    {
        var internalIn = new byte[CanFrame.IdBytes + 2];
        CanFrame.WriteId(internalIn, 0x7E0);
        internalIn[CanFrame.IdBytes] = 0xAA;
        internalIn[CanFrame.IdBytes + 1] = 0xBB;

        var wire = new byte[RawCanWire.FrameSize];
        // Pre-dirty the buffer to prove FromInternal clears the tail.
        for (int i = 0; i < wire.Length; i++) wire[i] = 0xFF;
        RawCanWire.FromInternal(internalIn, wire);

        // bytes after the 2 valid data bytes (index 1+4+2 = 7 onward) are zero.
        for (int i = 1 + CanFrame.IdBytes + 2; i < RawCanWire.FrameSize; i++)
            Assert.Equal(0, wire[i]);
    }

    [Fact]
    public void Payload_longer_than_eight_is_clamped_on_egress()
    {
        // Defensive: a malformed internal frame with > 8 payload bytes should
        // still produce a valid 13-byte wire frame (DLC clamped to 8).
        var internalIn = new byte[CanFrame.IdBytes + 12];
        CanFrame.WriteId(internalIn, 0x7E0);
        Span<byte> wire = stackalloc byte[RawCanWire.FrameSize];
        RawCanWire.FromInternal(internalIn, wire);
        Assert.Equal(RawCanWire.MaxData, wire[0] & 0x0F);
    }

    [Fact]
    public void ToInternal_rejects_short_buffer()
        => Assert.Throws<ArgumentException>(() => RawCanWire.ToInternal(new byte[RawCanWire.FrameSize - 1]));

    [Fact]
    public void FromInternal_rejects_short_destination()
    {
        var internalIn = new byte[CanFrame.IdBytes + 1];
        Assert.Throws<ArgumentException>(() =>
        {
            var tooSmall = new byte[RawCanWire.FrameSize - 1];
            RawCanWire.FromInternal(internalIn, tooSmall);
        });
    }
}
