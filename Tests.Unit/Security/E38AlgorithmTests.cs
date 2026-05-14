using System.Text.Json;
using Common.Protocol;
using Core.Security;
using Core.Security.Algorithms;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Security;

// Pinpoints the documented E38 algorithm with several seed→key pairs computed
// from the reference C source, then exercises the whole exchange end-to-end
// via Service27Handler. If/when a real seed/key capture is obtained, add it
// to ComputedTestVectors below.
public sealed class E38AlgorithmTests
{
    [Theory]
    [InlineData((ushort)0x1234, (ushort)0x96CE)]
    [InlineData((ushort)0xA1B2, (ushort)0x0750)]
    [InlineData((ushort)0xDEAD, (ushort)0xCA54)]
    [InlineData((ushort)0xCAFE, (ushort)0xDE03)]
    [InlineData((ushort)0xFFFF, (ushort)0xA902)]
    public void ComputeKey_MatchesDocumentedVectors(ushort seed, ushort expectedKey)
    {
        Assert.Equal(expectedKey, E38Algorithm.ComputeKey(seed));
    }

    [Fact]
    public void EndToEnd_FixedSeed_CorrectKey_Unlocks()
    {
        var algo = new E38Algorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "1234" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-e38-test"));
        var ch = NodeFactory.CreateChannel();

        // requestSeed
        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x12, 0x34 },
                     TestFrame.DequeueSingleFrameUsdt(ch));

        // sendKey with the correct E38(0x1234) = 0x96CE
        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0x96, 0xCE }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(1, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void EndToEnd_FixedSeed_WrongKey_Nrc35()
    {
        var algo = new E38Algorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "1234" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-e38-test"));
        var ch = NodeFactory.CreateChannel();

        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        TestFrame.DequeueSingleFrameUsdt(ch);

        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0xDE, 0xAD }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(0, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void RandomSeed_RoundTrips_WithComputedKey()
    {
        // No fixedSeed → random per request. Capture whatever the module emits,
        // recompute the key with the documented algorithm, send it back, expect unlock.
        var algo = new E38Algorithm();
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-e38-test"));
        var ch = NodeFactory.CreateChannel();

        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        var seedResp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(Service.Positive(Service.SecurityAccess), seedResp[0]);
        Assert.Equal(0x01, seedResp[1]);
        ushort seed = (ushort)((seedResp[2] << 8) | seedResp[3]);
        ushort key = E38Algorithm.ComputeKey(seed);

        Service27Handler.Handle(node,
            new byte[] { 0x27, 0x02, (byte)(key >> 8), (byte)(key & 0xFF) },
            ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    // ----- Programming-session policy -----
    //
    // E38 bootloader enforces the same GMLAN 0x92 algorithm as the OS - this
    // is the inverse of the T43 boot-block stub. The test asserts both the
    // declared policy and the runtime behaviour after $10 $02.

    [Fact]
    public void Policy_DeclaresUnchangedAlgorithm()
    {
        Assert.Equal(ProgrammingSessionBehavior.UnchangedAlgorithm, new E38Algorithm().ProgrammingSession);
    }

    [Fact]
    public void EndToEnd_AfterDollar10Dollar02_StillRequiresRealKey()
    {
        // Same wire sequence as the T43 'unlocks via 00 00' test, but with
        // E38's UnchangedAlgorithm policy the bypass codepath stays cold.
        var algo = new E38Algorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "1234" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-e38-test"));
        var ch = NodeFactory.CreateChannel();

        Service10Handler.Handle(node, new byte[] { 0x10, 0x02 }, ch);
        TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.True(node.State.SecurityProgrammingShortcutActive);

        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        // Real seed comes back (not 00 00).
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x12, 0x34 },
                     TestFrame.DequeueSingleFrameUsdt(ch));

        // The hardcoded-zero key 6Speed.T43 would send fails.
        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0x00, 0x00 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey },
                     TestFrame.DequeueSingleFrameUsdt(ch));

        // The computed E38 key (= 0x96CE for seed 0x1234) succeeds even in
        // programming session.
        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0x96, 0xCE }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(1, node.State.SecurityUnlockedLevel);
    }
}
