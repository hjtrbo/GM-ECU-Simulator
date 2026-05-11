namespace Common.Waveforms;

public sealed class SquareWaveform : PeriodicWaveformBase
{
    private readonly double duty;

    public SquareWaveform(double amplitude, double offset, double freqHz, double phaseDeg, double duty)
        : base(amplitude, offset, freqHz, phaseDeg)
    {
        this.duty = Math.Clamp(duty, 0.0, 1.0);
    }

    protected override double SampleAtFoldedTime(double t)
    {
        double high = periodMs * duty;
        return (t < high ? amplitude : -amplitude) + offset;
    }
}
