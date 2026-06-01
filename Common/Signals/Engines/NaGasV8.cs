namespace Common.Signals.Engines;

// A naturally-aspirated warm gas V8 - the simulator's original character. The manifold only ever pulls vacuum, so MAP
// rises from deep idle vacuum up to (but never past) barometric at wide-open throttle. Selecting this reproduces the
// pre-pluggable engine behaviour byte-for-byte, which is what keeps it the safe default for existing configs.
public sealed class NaGasV8 : GasV8Character
{
    public override string Id => "na-gas-v8";
    public override string DisplayName => "Naturally Aspirated V8";

    // Vacuum-only curve anchored to barometric: baro*(0.18 + 0.0082*load) reaches exactly baro at 100% load and no
    // higher. No vacuum with the engine stopped - the manifold sits at barometric.
    protected override double ManifoldPressureKpa(in OperatingPoint op)
        => op.Running ? op.BaroKpa * (0.18 + 0.0082 * op.Load) : op.BaroKpa;
}
