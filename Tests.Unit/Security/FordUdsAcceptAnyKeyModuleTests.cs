using System.Text.Json;
using Core.Security.Modules;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Security;

// Direct coverage of the Ford accept-any-key $27 module, driven through
// Service27Handler so the egress wrapper runs end to end. The persona-level
// integration lives in FordUdsPersonaTests; this file pins the module's
// own behaviour: config-driven seed width / fixed seed, accept-any unlock, the
// already-unlocked zero-seed convention, and malformed-config fallback.
public sealed class FordUdsAcceptAnyKeyModuleTests
{
    private static (Core.Ecu.EcuNode node, Core.Bus.ChannelSession ch) Make(JsonElement? config = null)
    {
        var module = new FordUdsAcceptAnyKeyModule();
        module.LoadConfig(config);
        var node = NodeFactory.CreateNode(module);
        var ch = NodeFactory.CreateChannel();
        return (node, ch);
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void FixedSeed_IsReturnedVerbatim()
    {
        var (node, ch) = Make(Json("""{ "seedLength": 4, "fixedSeed": "11223344" }"""));
        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, 0);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x67, 0x01, 0x11, 0x22, 0x33, 0x44 }, resp);
    }

    [Fact]
    public void AnyKey_UnlocksAfterSeed()
    {
        var (node, ch) = Make(Json("""{ "fixedSeed": "ABCD" }"""));
        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, 0);
        TestFrame.DequeueSingleFrameUsdt(ch); // drain seed response

        // Key bytes are arbitrary - the module accepts whatever the tester sends.
        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0x00, 0x00 }, ch, 0);
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x67, 0x02 }, resp);
        Assert.Equal(1, (int)node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void AlreadyUnlocked_RequestSeed_IssuesFreshSeedAndAllowsReunlock()
    {
        // The Ford flash tool does NOT honour the "already unlocked -> zero seed,
        // skip sendKey" convention: after a higher-level unlock it runs a full
        // fresh requestSeed/sendKey at a lower level. So an already-unlocked
        // requestSeed must still issue a real seed AND track it as pending, or the
        // follow-up sendKey fails with NRC $22 (the bug this guards against).
        var (node, ch) = Make(Json("""{ "seedLength": 2, "fixedSeed": "ABCD" }"""));
        node.State.SecurityUnlockedLevel = 1;

        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, 0);
        var seedResp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x67, 0x01, 0xAB, 0xCD }, seedResp); // fresh seed, not 00 00

        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0x11, 0x22 }, ch, 0);
        var keyResp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x67, 0x02 }, keyResp);   // accepted, no $22
    }

    [Fact]
    public void Level2UnlockThenLevel1Handshake_BothSucceed()
    {
        // Regression for the captured FG flash flow: unlock level 2 ($27 03/04),
        // then a fresh level-1 handshake ($27 01/02) must also return 67 02 and
        // leave the controller unlocked - it previously NRC-$22'd the level-1
        // sendKey because the "already unlocked" path returned a zero seed with
        // no pending state. seedLength 3 matches the real FG PCM.
        var (node, ch) = Make(Json("""{ "seedLength": 3 }"""));

        Service27Handler.Handle(node, new byte[] { 0x27, 0x03 }, ch, 0);
        var l2Seed = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(0x67, l2Seed[0]);
        Assert.Equal(0x03, l2Seed[1]);
        Assert.Equal(5, l2Seed.Length);                      // 0x67 + sub + 3-byte seed
        Service27Handler.Handle(node, new byte[] { 0x27, 0x04, 0xEE, 0x4D, 0x93 }, ch, 0);
        Assert.Equal(new byte[] { 0x67, 0x04 }, TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(2, (int)node.State.SecurityUnlockedLevel);

        // Fresh level-1 handshake while already unlocked at level 2.
        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, 0);
        var l1Seed = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(0x67, l1Seed[0]);
        Assert.Equal(0x01, l1Seed[1]);
        bool anyNonZero = false;
        for (int i = 2; i < l1Seed.Length; i++) if (l1Seed[i] != 0) { anyNonZero = true; break; }
        Assert.True(anyNonZero, "level-1 seed must be fresh, not the zero already-unlocked seed");
        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0xDA, 0xA9, 0xC2 }, ch, 0);
        Assert.Equal(new byte[] { 0x67, 0x02 }, TestFrame.DequeueSingleFrameUsdt(ch));
        // Highest level reached is retained - level 1 unlock must not downgrade from 2.
        Assert.Equal(2, (int)node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void MalformedFixedSeed_FallsBackToRandomNonZeroSeed()
    {
        // "ZZ" is not valid hex for seedLength=2 -> ignored, random seed used.
        var (node, ch) = Make(Json("""{ "seedLength": 2, "fixedSeed": "ZZ" }"""));
        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, 0);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(0x67, resp[0]);
        Assert.Equal(4, resp.Length);                       // 0x67 + sub + 2-byte seed
        Assert.True(resp[2] != 0 || resp[3] != 0, "fallback seed must be non-zero");
    }
}
