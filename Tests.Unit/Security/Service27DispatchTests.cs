using Common.Protocol;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Security;

public sealed class Service27DispatchTests
{
    [Fact]
    public void NoModule_Returns_NrcServiceNotSupported()
    {
        var node = NodeFactory.CreateNode(module: null);
        var ch = NodeFactory.CreateChannel();

        bool activated = Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);

        Assert.False(activated);
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.ServiceNotSupported }, resp);
    }

    [Fact]
    public void ModuleConfigured_ReturnsTrue_AndModuleIsInvoked()
    {
        var algo = new FakeSeedKeyAlgorithm();
        var node = NodeFactory.CreateNodeWithGenericModule(algo);
        var ch = NodeFactory.CreateChannel();

        bool activated = Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);

        // $27 activates P3C even when the response is an NRC: the request is
        // still enhanced traffic that should refresh the keepalive window.
        Assert.True(activated);
        // FakeSeedKeyAlgorithm by default returns [0x12, 0x34] → positive response.
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x12, 0x34 }, resp);
    }
}
