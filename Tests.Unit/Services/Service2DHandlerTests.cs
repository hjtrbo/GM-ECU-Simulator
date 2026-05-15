using Common.Protocol;
using Core.Ecu;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// GMW3110-2010 §8.11 DefinePIDByAddress ($2D) coverage. The simulator maps
// "memory addresses" onto existing configured PIDs (a sim approximation of
// the spec's RAM-address model). Tests pin the validation order and the
// alias-into-existing-Pid semantics.
public sealed class Service2DHandlerTests
{
    private static EcuNode NodeWithPid(uint address)
    {
        var node = NodeFactory.CreateNode();
        node.AddPid(new Pid { Address = address, Size = PidSize.Byte });
        return node;
    }

    [Fact]
    public void Define_ValidRequest_RegistersDynamicPid()
    {
        var node = NodeWithPid(0x000C);
        var ch = NodeFactory.CreateChannel();

        // SID + PID(2) + addr(2) + MS = 6 bytes total.
        bool ok = Service2DHandler.Handle(node, new byte[] { 0x2D, 0xCC, 0xFF, 0x00, 0x0C, 0x01 }, ch);

        Assert.True(ok);
        Assert.Equal(new byte[] { 0x6D, 0xCC, 0xFF }, TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.NotNull(node.GetPid(0xCCFF));
        Assert.Contains(0xCCFFu, node.State.DynamicallyDefinedPids);
    }

    [Fact]
    public void TooShort_ReturnsNrc12()
    {
        // §8.11.4: incorrect length. Minimum is SID + PID(2) + addr(2) + MS = 6.
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        bool ok = Service2DHandler.Handle(node, new byte[] { 0x2D, 0xCC, 0xFF, 0x00 }, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.DefinePidByAddress, Nrc.SubFunctionNotSupportedInvalidFormat },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void MemorySizeZero_ReturnsNrc31()
    {
        // §8.11.4 NRC $31: memorySize equal to 0.
        var node = NodeWithPid(0x000C);
        var ch = NodeFactory.CreateChannel();

        bool ok = Service2DHandler.Handle(node, new byte[] { 0x2D, 0xCC, 0xFF, 0x00, 0x0C, 0x00 }, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.DefinePidByAddress, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void MemorySizeOverSeven_ReturnsNrc31()
    {
        // §8.11.4 NRC $31: memorySize > 7.
        var node = NodeWithPid(0x000C);
        var ch = NodeFactory.CreateChannel();

        bool ok = Service2DHandler.Handle(node, new byte[] { 0x2D, 0xCC, 0xFF, 0x00, 0x0C, 0x08 }, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.DefinePidByAddress, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void UnknownAddress_ReturnsNrc31()
    {
        // §8.11.4 NRC $31: address invalid - the sim has no PID configured at
        // the requested address.
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        bool ok = Service2DHandler.Handle(node, new byte[] { 0x2D, 0xCC, 0xFF, 0xDE, 0xAD, 0x01 }, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.DefinePidByAddress, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void CollisionWithStaticPid_ReturnsNrc31()
    {
        // Sim safety check: refusing to overwrite a statically-configured PID
        // protects existing config from being silently replaced on $20 exit.
        // The static PID at $0042 is the collision target; we try to alias
        // 0x000C under that same id.
        var node = NodeFactory.CreateNode();
        node.AddPid(new Pid { Address = 0x000C, Size = PidSize.Byte });
        node.AddPid(new Pid { Address = 0x0042, Size = PidSize.Byte });
        var ch = NodeFactory.CreateChannel();

        bool ok = Service2DHandler.Handle(node, new byte[] { 0x2D, 0x00, 0x42, 0x00, 0x0C, 0x01 }, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.DefinePidByAddress, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void FourByteAddress_Accepted()
    {
        // §8.11.2 allows 2/3/4-byte memoryAddress. The simulator maps onto the
        // uint Address field so a 4-byte 0x00000005 round-trips just like the
        // 2-byte 0x0005 form.
        var node = NodeWithPid(0x0005);
        var ch = NodeFactory.CreateChannel();

        // SID + PID(2) + addr(4) + MS = 8 bytes total.
        bool ok = Service2DHandler.Handle(node, new byte[] { 0x2D, 0xCC, 0xEE, 0x00, 0x00, 0x00, 0x05, 0x01 }, ch);

        Assert.True(ok);
        Assert.Equal(new byte[] { 0x6D, 0xCC, 0xEE }, TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.NotNull(node.GetPid(0xCCEE));
    }
}
