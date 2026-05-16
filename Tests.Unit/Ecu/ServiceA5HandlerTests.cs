using Common.Protocol;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// GMW3110-2010 §8.17 ProgrammingMode ($A5) coverage.
//
// Wire-format intent comes from §8.17.5.1 Table 169 - $A5 is sent on the
// functional broadcast CAN ID ($101 / $FE) and each receiving programmable
// node responds on its physical response ID. The DPS Programmers Reference
// Manual page 241 shows the exact same trace template. The earlier physical-
// only tests in ProgrammingSequence{,CanProtocol}Tests cover the dispatch via
// $7E0 - this file fills the functional-broadcast gap that let a real DPS
// session silently stall at the $A5 step.
public sealed class ServiceA5HandlerTests
{
    [Fact]
    public void FunctionalA5_01_after_28_RespondsWithE5()
    {
        // Mirrors the DPS pre-utility-file sequence: $28 first, then
        // functional $A5 $01 -> $E5 on the ECU's USDT response ID.
        var node = NodeFactory.CreateNode();
        node.State.NormalCommunicationDisabled = true;
        var ch = NodeFactory.CreateChannel();

        var ok = ServiceA5Handler.Handle(node, new byte[] { 0xA5, 0x01 }, ch, isFunctional: true);

        Assert.True(ok);
        Assert.Equal(new byte[] { 0xE5 }, TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.True(node.State.ProgrammingModeRequested);
        Assert.False(node.State.ProgrammingHighSpeed);
    }

    [Fact]
    public void FunctionalA5_02_HighSpeed_RespondsWithE5_AndFlagsHighSpeed()
    {
        var node = NodeFactory.CreateNode();
        node.State.NormalCommunicationDisabled = true;
        var ch = NodeFactory.CreateChannel();

        var ok = ServiceA5Handler.Handle(node, new byte[] { 0xA5, 0x02 }, ch, isFunctional: true);

        Assert.True(ok);
        Assert.Equal(new byte[] { 0xE5 }, TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.True(node.State.ProgrammingHighSpeed);
    }

    [Fact]
    public void FunctionalA5_03_after_A5_01_ActivatesAndStaysSilent()
    {
        // §8.17.3 footnote M2: "There is no response to a request message with
        // a sub-parameter value of $03." Verify both the silence and the
        // state transition into ProgrammingModeActive.
        var node = NodeFactory.CreateNode();
        node.State.NormalCommunicationDisabled = true;
        node.State.ProgrammingModeRequested = true;  // implicit prior $A5 $01
        var ch = NodeFactory.CreateChannel();

        var ok = ServiceA5Handler.Handle(node, new byte[] { 0xA5, 0x03 }, ch, isFunctional: true);

        Assert.True(ok);  // handler signals success to keep P3C alive
        TestFrame.AssertEmpty(ch);
        Assert.True(node.State.ProgrammingModeActive);
        Assert.True(node.State.SecurityProgrammingShortcutActive);
    }

    [Fact]
    public void FunctionalA5_without_28_IsSilent()
    {
        // §8.17.4 NRC $22 on physical addressing. On functional broadcast,
        // suppressed so silent ECUs do not blanket the bus with NRCs.
        var node = NodeFactory.CreateNode();
        // NormalCommunicationDisabled stays at default false.
        var ch = NodeFactory.CreateChannel();

        var ok = ServiceA5Handler.Handle(node, new byte[] { 0xA5, 0x01 }, ch, isFunctional: true);

        Assert.False(ok);
        TestFrame.AssertEmpty(ch);
        Assert.False(node.State.ProgrammingModeRequested);
    }

    [Fact]
    public void FunctionalA5_03_without_prior_request_IsSilent()
    {
        // Bus-storm-avoidance corner case: ECUs that didn't see $A5 $01 first
        // must not emit $7F A5 22 in unison when the functional $A5 $03 lands.
        var node = NodeFactory.CreateNode();
        node.State.NormalCommunicationDisabled = true;
        // ProgrammingModeRequested stays at default false.
        var ch = NodeFactory.CreateChannel();

        var ok = ServiceA5Handler.Handle(node, new byte[] { 0xA5, 0x03 }, ch, isFunctional: true);

        Assert.False(ok);
        TestFrame.AssertEmpty(ch);
        Assert.False(node.State.ProgrammingModeActive);
    }

    [Fact]
    public void FunctionalA5_BadSubFunction_IsSilent()
    {
        // §8.17.4 NRC $12 on physical. Functional: stay silent.
        var node = NodeFactory.CreateNode();
        node.State.NormalCommunicationDisabled = true;
        var ch = NodeFactory.CreateChannel();

        var ok = ServiceA5Handler.Handle(node, new byte[] { 0xA5, 0x99 }, ch, isFunctional: true);

        Assert.False(ok);
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void FunctionalA5_BadLength_IsSilent()
    {
        // Length != 2 (just the SID, no sub-function). NRC $12 on physical;
        // silent on functional.
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        var ok = ServiceA5Handler.Handle(node, new byte[] { 0xA5 }, ch, isFunctional: true);

        Assert.False(ok);
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void PhysicalA5_01_without_28_StillReturnsNrc22()
    {
        // Sanity-check: the physical path's NRC behaviour is unchanged by
        // the functional refactor. If a tester sends $A5 $01 point-to-point
        // without $28 active, the spec wants $7F A5 22.
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        var ok = ServiceA5Handler.Handle(node, new byte[] { 0xA5, 0x01 }, ch, isFunctional: false);

        Assert.False(ok);
        Assert.Equal(
            new byte[] { Service.NegativeResponse, Service.ProgrammingMode, Nrc.ConditionsNotCorrectOrSequenceError },
            TestFrame.DequeueSingleFrameUsdt(ch));
    }
}
