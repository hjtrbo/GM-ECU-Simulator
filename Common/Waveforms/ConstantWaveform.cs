namespace Common.Waveforms;

// Returns a fixed engineering-unit value regardless of time. Useful for
// PIDs that should report a stuck reading (test fixtures, fault sims).
public sealed class ConstantWaveform : IWaveformGenerator
{
    private readonly double value;
    public ConstantWaveform(double value) { this.value = value; }
    public double Sample(double timeMs) => value;
}
