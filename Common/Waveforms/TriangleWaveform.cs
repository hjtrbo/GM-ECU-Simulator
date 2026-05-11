namespace Common.Waveforms;

public sealed class TriangleWaveform : PeriodicWaveformBase
{
    public TriangleWaveform(double amplitude, double offset, double freqHz, double phaseDeg)
        : base(amplitude, offset, freqHz, phaseDeg) { }

    protected override double SampleAtFoldedTime(double t)
    {
        double half = periodMs / 2.0;
        // -A → +A → -A across one period
        double normalized = t < half ? (t / half) * 2.0 - 1.0 : 1.0 - ((t - half) / half) * 2.0;
        return amplitude * normalized + offset;
    }
}
