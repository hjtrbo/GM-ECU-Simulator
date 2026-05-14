using Common.Protocol;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// GMW3110-2010 §8.16 ReportProgrammedState ($A2) coverage.
public sealed class ServiceA2HandlerTests
{
    [Fact]
    public void DefaultState_ReturnsE2_00_FullyProgrammed()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        var ok = ServiceA2Handler.Handle(node, new byte[] { 0xA2 }, ch);

        Assert.True(ok);
        Assert.Equal(new byte[] { 0xE2, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Theory]
    [InlineData((byte)0x01)] // NSC
    [InlineData((byte)0x02)] // NC
    [InlineData((byte)0x50)] // GMF
    [InlineData((byte)0x54)] // FMF
    public void ConfiguredState_RoundTrips(byte state)
    {
        var node = NodeFactory.CreateNode();
        node.ProgrammedState = state;
        var ch = NodeFactory.CreateChannel();

        ServiceA2Handler.Handle(node, new byte[] { 0xA2 }, ch);

        Assert.Equal(new byte[] { 0xE2, state }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void RequestWithExtraBytes_ReturnsNrc12()
    {
        // §8.16.4 SFNS-IF: request must be SID only.
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        var ok = ServiceA2Handler.Handle(node, new byte[] { 0xA2, 0x00 }, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.ReportProgrammedState, Nrc.SubFunctionNotSupportedInvalidFormat },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }
}
