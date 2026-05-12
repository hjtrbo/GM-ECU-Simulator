using Common.Protocol;
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
}
