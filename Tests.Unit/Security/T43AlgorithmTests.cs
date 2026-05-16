using System.Text.Json;
using Common.Protocol;
using Core.Security;
using Core.Security.Algorithms;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Security;

// Pinpoints the T43 algorithm (decompiled from 6Speed.T43's gett43key) with
// hand-traced seed->key pairs, then exercises the whole exchange end-to-end
// via Service27Handler. If/when a real seed/key capture is obtained from
// hardware, add it to ComputedTestVectors below.
public sealed class T43AlgorithmTests
{
    [Theory]
    [InlineData((ushort)0x0000, (ushort)0x4279)]
    [InlineData((ushort)0x1234, (ushort)0xA1E7)]
    [InlineData((ushort)0xDEAD, (ushort)0xDB83)]
    [InlineData((ushort)0xCAFE, (ushort)0x5421)]
    [InlineData((ushort)0xFFFF, (ushort)0x4A79)]
    public void ComputeKey_MatchesDocumentedVectors(ushort seed, ushort expectedKey)
    {
        Assert.Equal(expectedKey, T43Algorithm.ComputeKey(seed));
    }

    [Fact]
    public void EndToEnd_FixedSeed_CorrectKey_Unlocks()
    {
        var algo = new T43Algorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "1234" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-t43"));
        var ch = NodeFactory.CreateChannel();

        // requestSeed
        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x12, 0x34 },
                     TestFrame.DequeueSingleFrameUsdt(ch));

        // sendKey with the correct T43(0x1234) = 0xA1E7
        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0xA1, 0xE7 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(1, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void EndToEnd_FixedSeed_WrongKey_Nrc35()
    {
        var algo = new T43Algorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "1234" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-t43"));
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
        // No fixedSeed -> random per request. Capture whatever the module emits,
        // recompute the key with the documented algorithm, send it back, expect unlock.
        var algo = new T43Algorithm();
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-t43"));
        var ch = NodeFactory.CreateChannel();

        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        var seedResp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(Service.Positive(Service.SecurityAccess), seedResp[0]);
        Assert.Equal(0x01, seedResp[1]);
        ushort seed = (ushort)((seedResp[2] << 8) | seedResp[3]);
        ushort key = T43Algorithm.ComputeKey(seed);

        Service27Handler.Handle(node,
            new byte[] { 0x27, 0x02, (byte)(key >> 8), (byte)(key & 0xFF) },
            ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    // ----- Programming-session policy -----
    //
    // Real T43 boot block (file offset 0x2BBFC in a 24264923 image) returns
    // seed = 00 00 and accepts any key. 6Speed.T43 relies on this and sends
    // the literal $27 $02 00 00 regardless of what the algorithm would
    // compute. The simulator models this by declaring BypassAll and flipping
    // ProgrammingModeActive via $10 $02 (Service10Handler).

    [Fact]
    public void Policy_DeclaresBypassAll()
    {
        Assert.Equal(ProgrammingSessionBehavior.BypassAll, new T43Algorithm().ProgrammingSession);
    }

    [Fact]
    public void EndToEnd_SixSpeedT43Wire_UnlocksAfterDollar10Dollar02()
    {
        // Mirrors the wire trace 6Speed.T43 actually emits:
        //   $10 $02 -> $50 $02
        //   $27 $01 -> $67 $01 00 00
        //   $27 $02 00 00 -> $67 $02
        // With a non-zero fixedSeed configured the operational-mode algorithm
        // would reject the hardcoded 00 00 key, but $10 $02 puts the ECU into
        // programming session and BypassAll kicks in.
        var algo = new T43Algorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "B34C" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-t43"));
        var ch = NodeFactory.CreateChannel();

        // $10 $02 - enter programming session (security-shortcut path)
        Service10Handler.Handle(node, new byte[] { 0x10, 0x02 }, ch);
        Assert.Equal(new byte[] { Service.Positive(Service.InitiateDiagnosticOperation), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.True(node.State.SecurityProgrammingShortcutActive);

        // $27 $01 - requestSeed
        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        // Bypass: seed = 00 00 instead of the configured B3 4C.
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x00, 0x00 },
                     TestFrame.DequeueSingleFrameUsdt(ch));

        // $27 $02 00 00 - the hardcoded key 6Speed.T43 actually sends
        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0x00, 0x00 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(1, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void EndToEnd_OperationalMode_RequiresRealKey()
    {
        // Without $10 $02, BypassAll is dormant. $27 $02 00 00 should fail
        // when the algorithm has a non-zero fixed seed - matching the wire
        // trace I diagnosed earlier (the NRC $35 we send before $10 $02 is
        // accepted).
        var algo = new T43Algorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "B34C" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-t43"));
        var ch = NodeFactory.CreateChannel();

        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0xB3, 0x4C },
                     TestFrame.DequeueSingleFrameUsdt(ch));

        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0x00, 0x00 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(0, node.State.SecurityUnlockedLevel);
    }
}
