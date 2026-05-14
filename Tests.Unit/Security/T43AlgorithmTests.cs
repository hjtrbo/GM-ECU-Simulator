using System.Text.Json;
using Common.Protocol;
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
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-t43-test"));
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
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-t43-test"));
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
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-t43-test"));
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
}
