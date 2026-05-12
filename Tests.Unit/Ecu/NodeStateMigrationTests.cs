using Common.PassThru;
using Core.Bus;
using Core.Ecu;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// Light regression coverage for the EcuNode → NodeState migration. Guards the
// biggest failure modes:
//   (a) NodeState defaults match the post-power-on "Normal Communication Mode"
//   (b) IsUnlocked / IsInLockout helpers behave as advertised
//   (c) ClearLastEnhancedChannelIf is CAS-correct (only clears when matching)
public sealed class NodeStateMigrationTests
{
    [Fact]
    public void DefaultConstruction_Yields_PostPowerOnState()
    {
        var s = new NodeState();

        Assert.NotNull(s.Dpids);
        Assert.Empty(s.Dpids);
        Assert.NotNull(s.DynamicallyDefinedPids);
        Assert.Empty(s.DynamicallyDefinedPids);
        Assert.Null(s.LastEnhancedChannel);
        Assert.Equal(0, s.SecurityUnlockedLevel);
        Assert.Equal(0, s.SecurityPendingSeedLevel);
        Assert.Null(s.SecurityLastIssuedSeed);
        Assert.Equal(0, s.SecurityFailedAttempts);
        Assert.Equal(0, s.SecurityLockoutUntilMs);
        Assert.Null(s.SecurityModuleState);
    }

    [Fact]
    public void IsUnlocked_TrueOnlyWhenAtOrAboveRequestedLevel()
    {
        var s = new NodeState();
        Assert.False(s.IsUnlocked(1));

        s.SecurityUnlockedLevel = 2;
        Assert.True(s.IsUnlocked(1));
        Assert.True(s.IsUnlocked(2));
        Assert.False(s.IsUnlocked(3));
        Assert.False(s.IsUnlocked(0)); // level 0 is "no level", always false
    }

    [Fact]
    public void IsInLockout_StrictlyGreater()
    {
        var s = new NodeState { SecurityLockoutUntilMs = 1000 };
        Assert.True(s.IsInLockout(0));
        Assert.True(s.IsInLockout(999));
        Assert.False(s.IsInLockout(1000));
        Assert.False(s.IsInLockout(1001));
    }

    [Fact]
    public void ClearLastEnhancedChannelIf_OnlyClearsMatchingChannel()
    {
        var s = new NodeState();
        var ch1 = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000 };
        var ch2 = new ChannelSession { Id = 2, Protocol = ProtocolID.CAN, Baud = 500_000 };
        s.LastEnhancedChannel = ch1;

        // CAS with a non-matching channel must NOT clear.
        s.ClearLastEnhancedChannelIf(ch2);
        Assert.Same(ch1, s.LastEnhancedChannel);

        // CAS with the matching channel clears it.
        s.ClearLastEnhancedChannelIf(ch1);
        Assert.Null(s.LastEnhancedChannel);
    }

    [Fact]
    public void AddDpid_StoresByDpidId()
    {
        var s = new NodeState();
        var dpid = new Dpid { Id = 0xF0, Pids = Array.Empty<Pid>() };

        s.AddDpid(dpid);

        Assert.True(s.Dpids.TryGetValue(0xF0, out var got));
        Assert.Same(dpid, got);
    }
}
