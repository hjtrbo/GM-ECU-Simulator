using Core.Replay;

namespace EcuSimulator.Tests.Replay;

public class NodeTypeMapperTests
{
    [Theory]
    [InlineData(0x7E8, "ECM",  0x7E0, 0x7E8, 0x5E8)]
    [InlineData(0x7E9, "TCM",  0x7E1, 0x7E9, 0x5E9)]
    [InlineData(0x7EA, "BCM",  0x7E2, 0x7EA, 0x5EA)]
    [InlineData(0x7EB, "FPCM", 0x7E3, 0x7EB, 0x5EB)]
    public void KnownNodeTypes_MapToObdConvention(
        ushort nodeType, string name, ushort req, ushort usdt, ushort uudt)
    {
        var t = NodeTypeMapper.FromNodeType(nodeType);
        Assert.NotNull(t);
        Assert.Equal(name, t!.Name);
        Assert.Equal(req,  t.PhysicalRequestCanId);
        Assert.Equal(usdt, t.UsdtResponseCanId);
        Assert.Equal(uudt, t.UudtResponseCanId);
    }

    [Fact]
    public void UnknownNodeType_GetsSyntheticName()
    {
        var t = NodeTypeMapper.FromNodeType(0x5F1);
        Assert.NotNull(t);
        Assert.Equal("NODE_5F1", t!.Name);
        Assert.Equal((ushort)0x5E9, t.PhysicalRequestCanId);
        Assert.Equal((ushort)0x5F1, t.UsdtResponseCanId);
    }

    [Fact]
    public void NoneNodeType_ReturnsNull()
    {
        Assert.Null(NodeTypeMapper.FromNodeType(0));
    }
}
