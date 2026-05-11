using Common.Waveforms;

namespace Core.Replay;

// Bridges Pid.Waveform to a BinReplayCoordinator + channel index. Stateless
// past those two fields; the coordinator owns all the replay state and
// takes care of thread safety on its end.
public sealed class ReplayWaveform : IWaveformGenerator
{
    private readonly BinReplayCoordinator coordinator;
    private readonly int channelIndex;

    public ReplayWaveform(BinReplayCoordinator coordinator, int channelIndex)
    {
        this.coordinator = coordinator;
        this.channelIndex = channelIndex;
    }

    public int ChannelIndex => channelIndex;

    public double Sample(double timeMs) => coordinator.Sample(channelIndex, timeMs);
}
