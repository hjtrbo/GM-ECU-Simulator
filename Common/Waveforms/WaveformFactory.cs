namespace Common.Waveforms;

// Constructs the IWaveformGenerator for a synthetic-shape WaveformConfig. FileStream is intentionally NOT handled here
// - it represents bin-replay data which only Pid can resolve (it owns the replay-waveform factory). Pid routes around
// the factory when Shape == FileStream and only forwards the synthetic shapes here, so the FileStream branch in the
// switch would be unreachable; we throw to surface a misuse instead.
public static class WaveformFactory
{
    public static IWaveformGenerator Create(WaveformConfig cfg) => cfg.Shape switch
    {
        WaveformShape.Sin        => new SinWaveform(cfg.Amplitude, cfg.Offset, cfg.FrequencyHz, cfg.PhaseDeg),
        WaveformShape.Triangle   => new TriangleWaveform(cfg.Amplitude, cfg.Offset, cfg.FrequencyHz, cfg.PhaseDeg),
        WaveformShape.Square     => new SquareWaveform(cfg.Amplitude, cfg.Offset, cfg.FrequencyHz, cfg.PhaseDeg, cfg.DutyCycle),
        WaveformShape.Sawtooth   => new SawtoothWaveform(cfg.Amplitude, cfg.Offset, cfg.FrequencyHz, cfg.PhaseDeg),
        WaveformShape.Constant   => new ConstantWaveform(cfg.Offset),
        WaveformShape.FileStream => throw new InvalidOperationException("FileStream is bin-replay data; Pid resolves it via SetReplayWaveformFactory, not WaveformFactory."),
                               _ => throw new ArgumentOutOfRangeException(nameof(cfg.Shape)),
    };
}
