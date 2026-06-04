using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Ecu.Personas;
using Core.Persistence;
using Core.Scheduler;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.RawCan;

// Switching mode (or connection type) must fully re-initialise every ECU so no
// session remnants from one transport contaminate the other. There are two
// distinct teardowns in MainViewModel.ChangeSelection:
//   - A true MODE change rebuilds from disk:
//       bus.ReplaceNodes(empty) -> ConfigStore.ApplyTo(cfg, bus) -> Rebuild().
//   - A connection-type-only flip (same mode) resets each ECU IN PLACE via
//       EcuViewModel.ResetEcuState (EcuExitLogic.Run + ResetSecurityState),
//     which keeps the loaded config (and its persona) instead of reloading.
// This file covers the first path at the bus/config layer (no WPF, no confirm
// dialog): a node mutated to look like a used session is rebuilt from its config
// and must come back clean. The in-place reset path is covered by
// EcuExitLogicPersonaTests (persona retention) and the security reset tests.
public sealed class ConnectionSwitchReinitTests
{
    [Fact]
    public void Reinit_rebuilds_a_clean_ecu_with_no_session_remnants()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNodeWithGenericModule();
        node.AddPid(new Pid { Address = 0x000C, Size = PidSize.Byte });
        bus.AddNode(node);

        // Snapshot the config BEFORE dirtying runtime state - session state is
        // not part of the config DTO, which is exactly the point.
        var cfg = ConfigStore.Snapshot(bus);

        // Dirty the node to mimic an active J2534 session.
        var pid = node.GetPid(0x000C)!;
        node.State.AddDpid(new Dpid { Id = 0xFE, Pids = new[] { pid } });
        node.State.DynamicallyDefinedPids.Add(0x1234);
        node.State.SecurityUnlockedLevel = 1;
        node.State.ProgrammingModeActive = true;
        node.State.DownloadActive = true;
        node.State.TesterPresent.Activate();
        node.Persona = UdsKernelPersona.Instance;
        bus.Scheduler.Add(node, node.State.Dpids[0xFE], NodeFactory.CreateChannel(), DpidRate.Slow);

        // Sanity: the state really is dirty.
        Assert.NotEmpty(node.State.Dpids);
        Assert.Equal(1, node.State.SecurityUnlockedLevel);

        // The re-init the connection-type flip performs.
        bus.ReplaceNodes(Array.Empty<EcuNode>());
        ConfigStore.ApplyTo(cfg, bus);

        var rebuilt = Assert.Single(bus.Nodes);
        Assert.NotSame(node, rebuilt);                                 // a fresh instance
        Assert.Empty(rebuilt.State.Dpids);                             // no carried DPIDs
        Assert.Empty(rebuilt.State.DynamicallyDefinedPids);            // no $2D remnants
        Assert.Equal(0, rebuilt.State.SecurityUnlockedLevel);          // security re-locked
        Assert.False(rebuilt.State.ProgrammingModeActive);
        Assert.False(rebuilt.State.DownloadActive);
        Assert.Equal(TesterPresentTimerState.Inactive, rebuilt.State.TesterPresent.State);
        Assert.Same(Gmw3110Persona.Instance, rebuilt.Persona);         // persona reset
        Assert.NotNull(rebuilt.GetPid(0x000C));                        // config (the PID) survives
    }
}
