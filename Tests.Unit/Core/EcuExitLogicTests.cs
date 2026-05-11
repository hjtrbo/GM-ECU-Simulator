using Common.PassThru;
using Common.Protocol;
using Common.Waveforms;
using Core.Bus;
using Core.Ecu;
using Core.Services;

namespace EcuSimulator.Tests.Core;

public class EcuExitLogicTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    [Fact]
    public void ClearsAllEnhancedState()
    {
        var bus = new VirtualBus();
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        node.TesterPresent.Activate();
        node.AddPid(new Pid
        {
            Address = 0xFE40, Name = "dyn", Size = PidSize.Word,
            DataType = PidDataType.Unsigned,
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Constant, Offset = 0 },
        });
        lock (node.DynamicallyDefinedPids) node.DynamicallyDefinedPids.Add(0xFE40);

        var ch = NewChannel();
        EcuExitLogic.Run(node, bus.Scheduler, ch);

        Assert.Equal(TesterPresentTimerState.Inactive, node.TesterPresent.State);
        Assert.Null(node.GetPid(0xFE40));
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(0x60, msg!.Data[5]);                              // unsolicited positive $20 response
    }

    [Fact]
    public void Run_WithoutChannel_StillClearsState()
    {
        var bus = new VirtualBus();
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        node.TesterPresent.Activate();

        EcuExitLogic.Run(node, bus.Scheduler, respondOn: null);

        Assert.Equal(TesterPresentTimerState.Inactive, node.TesterPresent.State);
    }
}
