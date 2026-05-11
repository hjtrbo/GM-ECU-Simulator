namespace Common.Waveforms;

// Common period+phase folding for the periodic shapes (Triangle / Square /
// Sawtooth). Subclasses implement SampleAtFoldedTime(t) where t is in
// [0, periodMs).
public abstract class PeriodicWaveformBase : IWaveformGenerator
{
    protected readonly double amplitude;
    protected readonly double offset;
    protected readonly double periodMs;
    private readonly double phaseMs;

    protected PeriodicWaveformBase(double amplitude, double offset, double freqHz, double phaseDeg)
    {
        this.amplitude = amplitude;
        this.offset = offset;
        this.periodMs = 1000.0 / freqHz;
        this.phaseMs = (phaseDeg / 360.0) * periodMs;
    }

    public double Sample(double timeMs)
    {
        double t = (timeMs + phaseMs) % periodMs;
        if (t < 0) t += periodMs;
        return SampleAtFoldedTime(t);
    }

    protected abstract double SampleAtFoldedTime(double t);
}
