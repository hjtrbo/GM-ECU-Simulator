namespace Common.Waveforms;

public sealed class SawtoothWaveform : PeriodicWaveformBase
{
    public SawtoothWaveform(double amplitude, double offset, double freqHz, double phaseDeg)
        : base(amplitude, offset, freqHz, phaseDeg) { }

    protected override double SampleAtFoldedTime(double t)
    {
        // Ramp from -A to +A over one period
        return amplitude * (2.0 * t / periodMs - 1.0) + offset;
    }
}
