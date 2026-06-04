using Core.Bus;
using Core.Ecu.Personas;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// EcuExitLogic.Run reverts a runtime UDS-kernel handover back to GMW3110, but
// must NOT touch a *configured* persona. ResetEcuState (the connection-type
// flip / "Reset state" button) funnels through EcuExitLogic, so an
// unconditional persona reset silently reverted a loaded FordCapturePersona to
// gmw3110 - after which the capture sink NRC'd PCMTec's Mode $09 ($7F 09 11)
// instead of answering VIN/CalID. Regression guard for that.
public sealed class EcuExitLogicPersonaTests
{
    [Fact]
    public void Run_RevertsRuntimeUdsKernelHandover_ToGmw3110()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        bus.AddNode(node);

        // $36 sub $80 DownloadAndExecute swaps in the kernel persona at runtime;
        // $20 / P3C timeout is the documented hand-back point.
        node.Persona = UdsKernelPersona.Instance;

        EcuExitLogic.Run(node, bus.Scheduler, respondOn: null);

        Assert.Same(Gmw3110Persona.Instance, node.Persona);
    }

    [Fact]
    public void Run_PreservesConfiguredFordCapturePersona()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        bus.AddNode(node);

        // ford-capture is loaded from the config file (user state), not a runtime
        // handover - it must survive a reset/exit.
        node.Persona = FordCapturePersona.Instance;

        EcuExitLogic.Run(node, bus.Scheduler, respondOn: null);

        Assert.Same(FordCapturePersona.Instance, node.Persona);
    }

    [Fact]
    public void Run_LeavesStockGmw3110PersonaUntouched()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();          // default persona is gmw3110
        bus.AddNode(node);

        EcuExitLogic.Run(node, bus.Scheduler, respondOn: null);

        Assert.Same(Gmw3110Persona.Instance, node.Persona);
    }
}
