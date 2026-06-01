namespace Common.Signals.Engines;

// A boosted (supercharged / turbocharged) warm gas V8. Below the boost-onset load it behaves exactly like the NA
// motor - part-throttle cruise still pulls manifold vacuum - but as load climbs past the onset a forced-induction
// term lifts MAP above barometric, reaching baro + PeakBoostKpa at wide-open throttle. Because every airflow- and
// fuel-related signal reads MAP, the higher manifold pressure cascades for free: MAF rises with the denser charge,
// and the manifold-referenced fuel-pressure regulator climbs above its 4 bar base to hold injector differential
// under boost (the behaviour an NA character can never produce, since its MAP ceilings at barometric).
public sealed class BoostedGasV8 : GasV8Character
{
    public override string Id => "boosted-gas-v8";
    public override string DisplayName => "Boosted V8";

    // Peak gauge boost above barometric at wide-open throttle, in kPa. 109 kPa ~= 15.8 psi, putting WOT MAP at 210 kPa
    // absolute (101 baro + 109 boost). A plain knob the editor can expose; the registry constructs the default.
    public double PeakBoostKpa { get; init; } = 109.0;

    // Load (%) at which positive boost starts to build. Below it the engine is in vacuum on the NA curve; above it the
    // boost term ramps in. Throttle has to be open enough that blower/turbo output exceeds engine demand before the
    // manifold goes positive, so onset sits well up the load axis.
    public double BoostOnsetLoad { get; init; } = 70.0;

    protected override double ManifoldPressureKpa(in OperatingPoint op)
    {
        if (!op.Running) return op.BaroKpa;                          // engine stopped: manifold at barometric, no vacuum

        // The same vacuum curve as the NA motor up to the onset load.
        double vacuum = op.BaroKpa * (0.18 + 0.0082 * op.Load);
        if (op.Load <= BoostOnsetLoad) return vacuum;

        // Past onset, smoothstep from the vacuum value AT onset (so MAP is continuous across the threshold - no jump)
        // up to baro + PeakBoostKpa at 100% load. Smoothstep eases boost in rather than slamming it on.
        double vAtOnset = op.BaroKpa * (0.18 + 0.0082 * BoostOnsetLoad);
        double target = op.BaroKpa + PeakBoostKpa;
        double f = (op.Load - BoostOnsetLoad) / (100.0 - BoostOnsetLoad);
        f = f <= 0 ? 0 : f >= 1 ? 1 : f * f * (3 - 2 * f);
        return vAtOnset + (target - vAtOnset) * f;
    }
}
