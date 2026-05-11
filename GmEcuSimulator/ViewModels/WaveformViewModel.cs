using Common.Waveforms;
using Core.Ecu;

namespace GmEcuSimulator.ViewModels;

// Two-way bound editor for a Pid's waveform. Mutations rebuild the
// underlying IWaveformGenerator immediately so the next scheduler tick
// produces the new shape.
public sealed class WaveformViewModel : NotifyPropertyChangedBase
{
    private readonly Pid pid;

    public WaveformViewModel(Pid pid) { this.pid = pid; }

    private void Rebuild()
    {
        // Snapshot current settings, push back as a new WaveformConfig so the
        // factory rebuilds the IWaveformGenerator.
        pid.WaveformConfig = new WaveformConfig
        {
            Shape = Shape,
            Amplitude = Amplitude,
            Offset = Offset,
            FrequencyHz = FrequencyHz,
            PhaseDeg = PhaseDeg,
            DutyCycle = DutyCycle,
        };
    }

    public WaveformShape Shape
    {
        get => pid.WaveformConfig.Shape;
        set { if (pid.WaveformConfig.Shape != value) { pid.WaveformConfig.Shape = value; Rebuild(); OnPropertyChanged(); OnPropertyChanged(nameof(SupportsAmplitude)); OnPropertyChanged(nameof(SupportsFrequency)); OnPropertyChanged(nameof(SupportsDuty)); } }
    }

    public double Amplitude
    {
        get => pid.WaveformConfig.Amplitude;
        set { if (pid.WaveformConfig.Amplitude != value) { pid.WaveformConfig.Amplitude = value; Rebuild(); OnPropertyChanged(); } }
    }

    public double Offset
    {
        get => pid.WaveformConfig.Offset;
        set { if (pid.WaveformConfig.Offset != value) { pid.WaveformConfig.Offset = value; Rebuild(); OnPropertyChanged(); } }
    }

    public double FrequencyHz
    {
        get => pid.WaveformConfig.FrequencyHz;
        set { if (pid.WaveformConfig.FrequencyHz != value) { pid.WaveformConfig.FrequencyHz = value; Rebuild(); OnPropertyChanged(); } }
    }

    public double PhaseDeg
    {
        get => pid.WaveformConfig.PhaseDeg;
        set { if (pid.WaveformConfig.PhaseDeg != value) { pid.WaveformConfig.PhaseDeg = value; Rebuild(); OnPropertyChanged(); } }
    }

    public double DutyCycle
    {
        get => pid.WaveformConfig.DutyCycle;
        set { if (pid.WaveformConfig.DutyCycle != value) { pid.WaveformConfig.DutyCycle = value; Rebuild(); OnPropertyChanged(); } }
    }

    // Used by the XAML to disable irrelevant fields per shape. FileStream and Constant don't have an inherent
    // amplitude or frequency - FileStream takes its samples from the loaded bin, Constant is a fixed offset.
    public bool SupportsAmplitude => Shape != WaveformShape.Constant && Shape != WaveformShape.FileStream;
    public bool SupportsFrequency => Shape != WaveformShape.Constant && Shape != WaveformShape.FileStream;
    public bool SupportsDuty => Shape == WaveformShape.Square;
}
