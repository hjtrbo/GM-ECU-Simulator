namespace Common.Waveforms;

// Stateless sample function over wall-clock time. Implementations must be safe
// for concurrent reads — the scheduler thread and the UI thread both call it.
public interface IWaveformGenerator
{
    double Sample(double timeMs);
}
