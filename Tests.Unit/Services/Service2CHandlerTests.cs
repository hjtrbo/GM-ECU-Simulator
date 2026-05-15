using Common.Protocol;
using Core.Ecu;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// GMW3110-2010 §8.10 DynamicallyDefineMessage ($2C) coverage. Pins the
// validation order from §8.10.6.2 pseudo-code:
//   length / parity → DPID range → per-PID lookup → total-bytes ≤ 7.
public sealed class Service2CHandlerTests
{
    private static EcuNode NodeWithPids(params ushort[] addresses)
    {
        var node = NodeFactory.CreateNode();
        foreach (var a in addresses)
            node.AddPid(new Pid { Address = a, Size = PidSize.Byte });
        return node;
    }

    [Fact]
    public void Define_SinglePid_StoresDpid_AndEchoesPositive()
    {
        var node = NodeWithPids(0x000C);
        var ch = NodeFactory.CreateChannel();

        bool ok = Service2CHandler.Handle(node, new byte[] { 0x2C, 0xFE, 0x00, 0x0C }, ch);

        Assert.True(ok);
        Assert.Equal(new byte[] { 0x6C, 0xFE }, TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.True(node.State.Dpids.TryGetValue(0xFE, out var dpid));
        Assert.Single(dpid!.Pids);
        Assert.Equal(0x000Cu, dpid.Pids[0].Address);
    }

    [Theory]
    [InlineData((byte)0x00)]   // ReservedByDocument (no DPID)
    [InlineData((byte)0x80)]   // DTC range start
    [InlineData((byte)0x8F)]   // DTC range end
    [InlineData((byte)0xFF)]   // ReservedByDocument (FF)
    public void DpidInReservedRange_ReturnsNrc31(byte dpidId)
    {
        // §8.10.4 / Table 192: $00, $80-$8F, $FF are reserved.
        var node = NodeWithPids(0x000C);
        var ch = NodeFactory.CreateChannel();

        bool ok = Service2CHandler.Handle(node, new byte[] { 0x2C, dpidId, 0x00, 0x0C }, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.DynamicallyDefineMessage, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void UnsupportedPid_ReturnsNrc31()
    {
        // §8.10.4 NRC $31: a PID in the request is not supported by the node.
        var node = NodeWithPids(0x000C);
        var ch = NodeFactory.CreateChannel();

        bool ok = Service2CHandler.Handle(node, new byte[] { 0x2C, 0xFE, 0x99, 0x99 }, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.DynamicallyDefineMessage, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void TotalValueBytesExceedSeven_ReturnsNrc12()
    {
        // §8.10.4 NRC $12: combined PID widths exceed the 7-byte UUDT payload.
        // Eight Byte-sized PIDs at unique addresses give 8 total bytes.
        var addrs = new ushort[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        var node = NodeWithPids(addrs);
        var ch = NodeFactory.CreateChannel();

        var req = new byte[2 + addrs.Length * 2];
        req[0] = 0x2C;
        req[1] = 0xFE;
        for (int i = 0; i < addrs.Length; i++)
        {
            req[2 + i * 2] = (byte)(addrs[i] >> 8);
            req[3 + i * 2] = (byte)(addrs[i] & 0xFF);
        }

        bool ok = Service2CHandler.Handle(node, req, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.DynamicallyDefineMessage, Nrc.SubFunctionNotSupportedInvalidFormat },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void OddByteCount_ReturnsNrc12()
    {
        // §8.10.4 / pseudo-code: length MOD 2 != 0 → SFNS-IF.
        var node = NodeWithPids(0x000C);
        var ch = NodeFactory.CreateChannel();

        bool ok = Service2CHandler.Handle(node, new byte[] { 0x2C, 0xFE, 0x00, 0x0C, 0xFF }, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.DynamicallyDefineMessage, Nrc.SubFunctionNotSupportedInvalidFormat },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void NoPidBytes_ReturnsNrc12()
    {
        // §8.10.4: message must include DPID and at least one PID. SID + DPID
        // alone is too short.
        var node = NodeWithPids(0x000C);
        var ch = NodeFactory.CreateChannel();

        bool ok = Service2CHandler.Handle(node, new byte[] { 0x2C, 0xFE }, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.DynamicallyDefineMessage, Nrc.SubFunctionNotSupportedInvalidFormat },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }
}
