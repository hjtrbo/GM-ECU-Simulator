namespace Common.Waveforms;

public sealed class SinWaveform : IWaveformGenerator
{
    private readonly double amplitude;
    private readonly double offset;
    private readonly double freqHz;
    private readonly double phaseRad;

    public SinWaveform(double amplitude, double offset, double freqHz, double phaseDeg)
    {
        this.amplitude = amplitude;
        this.offset = offset;
        this.freqHz = freqHz;
        this.phaseRad = phaseDeg * Math.PI / 180.0;
    }

    public double Sample(double timeMs)
        => amplitude * Math.Sin(2.0 * Math.PI * freqHz * timeMs / 1000.0 + phaseRad) + offset;
}
