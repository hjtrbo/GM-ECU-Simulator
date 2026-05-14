using Common.Protocol;
using Core.Security;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Security;

// Drives the generic module through Service27Handler so the egress wrapper is
// exercised end-to-end. Uses a FakeSeedKeyAlgorithm whose seed + key + the
// success bit are set per-test.
public sealed class Gmw3110_2010_GenericTests
{
    private readonly FakeSeedKeyAlgorithm algo;
    private readonly Core.Ecu.EcuNode node;
    private readonly Core.Bus.ChannelSession ch;
    private long nowMs;

    public Gmw3110_2010_GenericTests()
    {
        algo = new FakeSeedKeyAlgorithm
        {
            SeedToReturn = new byte[] { 0x12, 0x34 },
            ExpectedKey = new byte[] { 0xAB, 0xCD },
            ComputeKeySucceeds = true,
        };
        node = NodeFactory.CreateNodeWithGenericModule(algo);
        ch = NodeFactory.CreateChannel();
        nowMs = 0;
    }

    private void Dispatch(params byte[] usdt) => Service27Handler.Handle(node, usdt, ch, nowMs);
    private byte[] Pop() => TestFrame.DequeueSingleFrameUsdt(ch);

    [Fact]
    public void RequestSeed_Level1_ReturnsSeed_AndStoresPending()
    {
        Dispatch(0x27, 0x01);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x12, 0x34 }, Pop());
        Assert.Equal(1, node.State.SecurityPendingSeedLevel);
        Assert.Equal(new byte[] { 0x12, 0x34 }, node.State.SecurityLastIssuedSeed);
        Assert.Equal(0, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void RequestSeed_WhenAlreadyUnlocked_ReturnsSeedAllZeros()
    {
        node.State.SecurityUnlockedLevel = 1;

        Dispatch(0x27, 0x01);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x00, 0x00 }, Pop());
    }

    [Fact]
    public void SendKey_WithoutPriorRequestSeed_ReturnsNrc22()
    {
        Dispatch(0x27, 0x02, 0xAB, 0xCD);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.ConditionsNotCorrectOrSequenceError }, Pop());
    }

    [Fact]
    public void SendKey_WithCorrectKey_Unlocks()
    {
        Dispatch(0x27, 0x01); Pop();
        Dispatch(0x27, 0x02, 0xAB, 0xCD);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 }, Pop());
        Assert.Equal(1, node.State.SecurityUnlockedLevel);
        Assert.Equal(0, node.State.SecurityFailedAttempts);
        Assert.Null(node.State.SecurityLastIssuedSeed);
        Assert.Equal(0, node.State.SecurityPendingSeedLevel);
    }

    [Fact]
    public void SendKey_WithWrongKey_OneAndTwo_ReturnsNrc35_IncrementsAttempts()
    {
        Dispatch(0x27, 0x01); Pop();

        Dispatch(0x27, 0x02, 0x00, 0x00);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey }, Pop());
        Assert.Equal(1, node.State.SecurityFailedAttempts);

        Dispatch(0x27, 0x02, 0x00, 0x00);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey }, Pop());
        Assert.Equal(2, node.State.SecurityFailedAttempts);
    }

    [Fact]
    public void SendKey_ThirdWrongKey_ReturnsNrc36_ArmsLockout()
    {
        Dispatch(0x27, 0x01); Pop();
        Dispatch(0x27, 0x02, 0x00, 0x00); Pop();
        Dispatch(0x27, 0x02, 0x00, 0x00); Pop();

        Dispatch(0x27, 0x02, 0x00, 0x00);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.ExceededNumberOfAttempts }, Pop());
        Assert.Equal(3, node.State.SecurityFailedAttempts);
        Assert.True(node.State.SecurityLockoutUntilMs > 0);
        Assert.Null(node.State.SecurityLastIssuedSeed); // pending seed invalidated on lockout
    }

    [Fact]
    public void RequestDuringLockout_ReturnsNrc37()
    {
        // Arm lockout manually.
        node.State.SecurityFailedAttempts = 3;
        node.State.SecurityLockoutUntilMs = 10_000;
        nowMs = 5_000; // inside the window

        Dispatch(0x27, 0x01);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.RequiredTimeDelayNotExpired }, Pop());
    }

    [Fact]
    public void Lockout_SelfHealsAfterDeadlinePasses_AndResetsAttempts()
    {
        node.State.SecurityFailedAttempts = 3;
        node.State.SecurityLockoutUntilMs = 10_000;
        nowMs = 10_001; // deadline elapsed

        Dispatch(0x27, 0x01);

        // Processed normally — seed comes back.
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x12, 0x34 }, Pop());
        Assert.Equal(0, node.State.SecurityFailedAttempts);
        Assert.Equal(0, node.State.SecurityLockoutUntilMs);
    }

    [Fact]
    public void SendKey_AlgorithmRefuses_ReturnsNrc35()
    {
        algo.ComputeKeySucceeds = false;
        Dispatch(0x27, 0x01); Pop();

        Dispatch(0x27, 0x02, 0xAB, 0xCD);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey }, Pop());
    }

    [Fact]
    public void Malformed_TooShort_ReturnsNrc12()
    {
        // $27 alone (no subfunction) — payload length 1.
        Dispatch(0x27);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.SubFunctionNotSupportedInvalidFormat }, Pop());
    }

    [Fact]
    public void Malformed_UnsupportedSubFunction_ReturnsNrc12()
    {
        // Sub-function 0x99 → level (0x99+1)/2 = 0x4D, not in SupportedLevels.
        Dispatch(0x27, 0x99);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.SubFunctionNotSupportedInvalidFormat }, Pop());
    }

    [Fact]
    public void Malformed_SubFunctionZero_ReturnsNrc12()
    {
        Dispatch(0x27, 0x00);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.SubFunctionNotSupportedInvalidFormat }, Pop());
    }

    [Fact]
    public void Malformed_RequestSeedWithExtraBytes_ReturnsNrc12()
    {
        // requestSeed (odd sub) must be exactly 2 bytes.
        Dispatch(0x27, 0x01, 0xFF);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.SubFunctionNotSupportedInvalidFormat }, Pop());
    }

    // ----- BypassSecurity short-circuit -----

    [Fact]
    public void Bypass_RequestSeed_EmitsZeroSeed_AndStoresPending()
    {
        node.BypassSecurity = true;
        algo.ComputeKeySucceeds = false; // would normally NRC the sendKey if reached

        Dispatch(0x27, 0x01);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x00, 0x00 }, Pop());
        Assert.Equal(1, node.State.SecurityPendingSeedLevel);
        Assert.Equal(new byte[] { 0x00, 0x00 }, node.State.SecurityLastIssuedSeed);
    }

    [Fact]
    public void Bypass_SendKey_AnyKey_Unlocks()
    {
        node.BypassSecurity = true;

        Dispatch(0x27, 0x01); Pop();
        Dispatch(0x27, 0x02, 0x00, 0x00); // the exact key 6Speed.T43 hardcodes

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 }, Pop());
        Assert.Equal(1, node.State.SecurityUnlockedLevel);
        Assert.Null(node.State.SecurityLastIssuedSeed);
        Assert.Equal(0, node.State.SecurityPendingSeedLevel);
        Assert.Equal(0, node.State.SecurityFailedAttempts);
    }

    [Fact]
    public void Bypass_SendKey_WithoutRequestSeed_StillUnlocks()
    {
        // Bypass skips the pending-seed precondition - a tester that goes
        // straight to sendKey still gets a positive response.
        node.BypassSecurity = true;

        Dispatch(0x27, 0x02, 0xDE, 0xAD);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 }, Pop());
        Assert.Equal(1, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void Bypass_OverridesLockout()
    {
        // Pre-existing lockout (e.g. left over from before bypass was toggled
        // on) must not block bypass-mode unlocks.
        node.State.SecurityLockoutUntilMs = 999_999;
        node.BypassSecurity = true;

        Dispatch(0x27, 0x01);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x00, 0x00 }, Pop());
    }

    [Fact]
    public void Bypass_SkipsAlgorithmLevelFilter()
    {
        // FakeSeedKeyAlgorithm only advertises level 1; bypass accepts any
        // level the tester asks for.
        node.BypassSecurity = true;

        Dispatch(0x27, 0x09); // sub 9 -> level 5, which the algo does NOT support

        Assert.Equal(Service.Positive(Service.SecurityAccess), Pop()[0]);
    }

    // ----- ProgrammingSession + BypassAll policy (T43-style) -----
    //
    // The boot-block stub behaviour is automatic when the algorithm declares
    // BypassAll and the ECU is in a programming session. No manual UI
    // toggle of BypassSecurity required.

    [Fact]
    public void ProgrammingSession_WithBypassAllAlgo_EmitsZeroSeed()
    {
        algo.ProgrammingSession = ProgrammingSessionBehavior.BypassAll;
        node.State.SecurityProgrammingShortcutActive = true;
        algo.ComputeKeySucceeds = false; // would NRC if we fell through to the algorithm

        Dispatch(0x27, 0x01);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x00, 0x00 }, Pop());
        Assert.Equal(new byte[] { 0x00, 0x00 }, node.State.SecurityLastIssuedSeed);
    }

    [Fact]
    public void ProgrammingSession_WithBypassAllAlgo_AcceptsAnyKey_IncludingZeros()
    {
        // The exact path 6Speed.T43 takes: hardcoded $27 $02 00 00.
        algo.ProgrammingSession = ProgrammingSessionBehavior.BypassAll;
        algo.ExpectedKey = new byte[] { 0xAB, 0xCD }; // real key, irrelevant in bypass
        node.State.SecurityProgrammingShortcutActive = true;

        Dispatch(0x27, 0x01); Pop();
        Dispatch(0x27, 0x02, 0x00, 0x00);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 }, Pop());
        Assert.Equal(1, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void ProgrammingSession_WithUnchangedAlgo_StillEnforcesAlgorithm()
    {
        // E38-style: programming session does not weaken security.
        algo.ProgrammingSession = ProgrammingSessionBehavior.UnchangedAlgorithm;
        node.State.SecurityProgrammingShortcutActive = true;

        Dispatch(0x27, 0x01);
        // Real seed comes back, not 00 00.
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x12, 0x34 }, Pop());

        Dispatch(0x27, 0x02, 0x00, 0x00); // wrong key
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey }, Pop());
        Assert.Equal(0, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void NotInProgrammingSession_BypassAllAlgo_StillEnforcesAlgorithm()
    {
        // BypassAll only kicks in WHEN a programming session is active. In
        // operational mode the T43 OS dispatcher at file offset 0xDC624 does
        // a strict cmpw against the static flash key, and the simulator
        // mirrors that.
        algo.ProgrammingSession = ProgrammingSessionBehavior.BypassAll;
        // node.State.ProgrammingModeActive defaults to false.

        Dispatch(0x27, 0x01);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x12, 0x34 }, Pop());

        Dispatch(0x27, 0x02, 0x00, 0x00); // wrong
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey }, Pop());
    }

    [Fact]
    public void ProgrammingSession_BypassAllAlgo_OverridesLockout()
    {
        // Lockout left over from operational-mode failed attempts should not
        // block the programming-session bypass path - real T43 lockout state
        // applies to the OS handler, not the boot block.
        algo.ProgrammingSession = ProgrammingSessionBehavior.BypassAll;
        node.State.SecurityProgrammingShortcutActive = true;
        node.State.SecurityFailedAttempts = 3;
        node.State.SecurityLockoutUntilMs = 999_999;
        nowMs = 5_000;

        Dispatch(0x27, 0x01);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x00, 0x00 }, Pop());
    }
}
