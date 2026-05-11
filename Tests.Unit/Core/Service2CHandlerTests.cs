using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Services;

namespace EcuSimulator.Tests.Core;

public class Service2CHandlerTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    [Fact]
    public void RegistersDpidWithReferencedPids()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();
        // SID 0x2C | DPID 0xFE | PID 0x1234 | PID 0x5678
        Service2CHandler.Handle(node, [0x2C, 0xFE, 0x12, 0x34, 0x56, 0x78], ch);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(0x6C, msg!.Data[5]);                 // positive response
        Assert.Equal(0xFE, msg.Data[6]);
        Assert.True(node.Dpids.ContainsKey(0xFE));
        Assert.Equal(2, node.Dpids[0xFE].Pids.Count);
    }

    [Fact]
    public void OverflowingPidValueBytes_ReturnsNrcInvalidFormat()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();
        // Both ECM PIDs are 2 bytes each. Four PIDs = 8 bytes > 7 byte UUDT limit.
        Service2CHandler.Handle(node, [0x2C, 0xFE, 0x12, 0x34, 0x56, 0x78, 0x12, 0x34, 0x56, 0x78], ch);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Nrc.SubFunctionNotSupportedInvalidFormat, msg!.Data[7]);
    }

    [Fact]
    public void UnknownPidId_ReturnsNrcRequestOutOfRange()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();
        Service2CHandler.Handle(node, [0x2C, 0xFE, 0xDE, 0xAD], ch);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Nrc.RequestOutOfRange, msg!.Data[7]);
    }

    [Fact]
    public void ReservedDpidId_ReturnsNrcRequestOutOfRange()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();
        // DPID 0x80 is in the reserved 0x80..0x8F band per GMW3110 §8.10.
        Service2CHandler.Handle(node, [0x2C, 0x80, 0x12, 0x34], ch);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Nrc.RequestOutOfRange, msg!.Data[7]);
    }
}
