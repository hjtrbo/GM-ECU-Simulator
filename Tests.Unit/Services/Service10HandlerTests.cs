using Common.Protocol;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// Verifies the UDS-compat shortcut: $10 $02 flips
// SecurityProgrammingShortcutActive so the $27 module's per-algorithm
// policy can short-circuit (the T43 boot-block stub flow that 6Speed.T43
// targets). Other subs stay as the existing pure-echo behaviour. The
// shortcut deliberately does NOT set ProgrammingModeActive - that flag
// gates $34 RequestDownload and is reserved for the strict GMW3110
// $28 + $A5 chain.
public sealed class Service10HandlerTests
{
    [Fact]
    public void Sub02_SetsSecurityShortcut_AndEchoesPositiveResponse()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Assert.False(node.State.SecurityProgrammingShortcutActive);
        Assert.False(node.State.ProgrammingModeActive);

        bool ok = Service10Handler.Handle(node, new byte[] { 0x10, 0x02 }, ch);

        Assert.True(ok);
        Assert.True(node.State.SecurityProgrammingShortcutActive);
        // Critically: ProgrammingModeActive stays false so a later $A5 $01
        // is not rejected as "programming-mode-already-active".
        Assert.False(node.State.ProgrammingModeActive);
        Assert.Equal(new byte[] { Service.Positive(Service.InitiateDiagnosticOperation), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Theory]
    [InlineData((byte)0x03)]   // enableDTCsDuringDevCntrl
    [InlineData((byte)0x04)]   // wakeUpLinks
    public void ValidNonProgrammingSubs_LeaveSecurityShortcutUnset(byte sub)
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service10Handler.Handle(node, new byte[] { 0x10, sub }, ch);

        Assert.False(node.State.SecurityProgrammingShortcutActive);
        Assert.Equal(new byte[] { Service.Positive(Service.InitiateDiagnosticOperation), sub },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Theory]
    [InlineData((byte)0x00)]   // ReservedByDocument
    [InlineData((byte)0x01)]   // ReservedByDocument
    [InlineData((byte)0x05)]   // ReservedByDocument (first byte past wakeUpLinks)
    [InlineData((byte)0x81)]   // far out of range
    [InlineData((byte)0xFF)]
    public void UndefinedSubs_ReturnNrc12(byte sub)
    {
        // §8.2.6.2 OTHERWISE branch and §8.2.4 Table 51 SFNS-IF: anything
        // outside {$02, $03, $04} must produce NRC $12.
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        bool ok = Service10Handler.Handle(node, new byte[] { 0x10, sub }, ch);

        Assert.False(ok);
        Assert.False(node.State.SecurityProgrammingShortcutActive);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.InitiateDiagnosticOperation, Nrc.SubFunctionNotSupportedInvalidFormat },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void MalformedRequest_DoesNotSetSecurityShortcut()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        // Missing sub-function byte.
        bool ok = Service10Handler.Handle(node, new byte[] { 0x10 }, ch);

        Assert.False(ok);
        Assert.False(node.State.SecurityProgrammingShortcutActive);
    }
}
