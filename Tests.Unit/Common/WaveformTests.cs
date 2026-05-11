using Common.Waveforms;

namespace EcuSimulator.Tests.Common;

public class WaveformTests
{
    [Fact]
    public void Sin_PeaksAndZeros()
    {
        var w = new SinWaveform(amplitude: 50, offset: 80, freqHz: 1.0, phaseDeg: 0);
        Assert.Equal(80, w.Sample(0), 6);             // sin(0) = 0 -> offset
        Assert.Equal(130, w.Sample(250), 4);          // sin(pi/2) = 1 -> amp + offset
        Assert.Equal(80, w.Sample(500), 4);           // sin(pi) = 0 -> offset
        Assert.Equal(30, w.Sample(750), 4);           // sin(3pi/2) = -1 -> -amp + offset
    }

    [Fact]
    public void Triangle_PeakAtQuarterCycle()
    {
        var w = new TriangleWaveform(amplitude: 1.0, offset: 0, freqHz: 1.0, phaseDeg: 0);
        Assert.Equal(-1.0, w.Sample(0), 6);
        Assert.Equal(0.0, w.Sample(250), 6);
        Assert.Equal(1.0, w.Sample(500), 6);
        Assert.Equal(0.0, w.Sample(750), 6);
    }

    [Theory]
    [InlineData(0.5, 0, 1)]
    [InlineData(0.5, 250, 1)]
    [InlineData(0.5, 499, 1)]
    [InlineData(0.5, 500, -1)]
    [InlineData(0.5, 999, -1)]
    [InlineData(0.25, 100, 1)]
    [InlineData(0.25, 300, -1)]
    public void Square_HighDuringDutyWindow(double duty, double timeMs, double expected)
    {
        var w = new SquareWaveform(amplitude: 1.0, offset: 0, freqHz: 1.0, phaseDeg: 0, duty: duty);
        Assert.Equal(expected, w.Sample(timeMs), 6);
    }

    [Fact]
    public void Sawtooth_RampsLinearly()
    {
        var w = new SawtoothWaveform(amplitude: 1.0, offset: 0, freqHz: 1.0, phaseDeg: 0);
        Assert.Equal(-1.0, w.Sample(0), 6);
        Assert.Equal(0.0, w.Sample(500), 6);
        Assert.True(w.Sample(999.999) > 0.99);
    }

    [Fact]
    public void Factory_BuildsCorrectImplementation()
    {
        Assert.IsType<SinWaveform>(WaveformFactory.Create(new WaveformConfig { Shape = WaveformShape.Sin, Amplitude = 1, FrequencyHz = 1 }));
        Assert.IsType<TriangleWaveform>(WaveformFactory.Create(new WaveformConfig { Shape = WaveformShape.Triangle, Amplitude = 1, FrequencyHz = 1 }));
        Assert.IsType<SquareWaveform>(WaveformFactory.Create(new WaveformConfig { Shape = WaveformShape.Square, Amplitude = 1, FrequencyHz = 1, DutyCycle = 0.5 }));
        Assert.IsType<SawtoothWaveform>(WaveformFactory.Create(new WaveformConfig { Shape = WaveformShape.Sawtooth, Amplitude = 1, FrequencyHz = 1 }));
    }

    // FileStream represents bin-replay data; only Pid resolves it (via SetReplayWaveformFactory). The factory
    // refusing to build it directly is the contract that keeps the bin-replay path the only way to produce a
    // FileStream generator.
    [Fact]
    public void Factory_ThrowsForFileStream()
    {
        Assert.Throws<InvalidOperationException>(
            () => WaveformFactory.Create(new WaveformConfig { Shape = WaveformShape.FileStream }));
    }
}
