namespace Common.Signals.Engines;

// Shared derivation for a warm gasoline V8: everything that is identical between induction variants - temperatures,
// the airflow shape, spark, electrical, fuel trims, O2 switching, and the manifold-referenced fuel-pressure
// regulator. The one piece a subclass supplies is the manifold-pressure curve (vacuum-only for NA, vacuum-plus-boost
// for forced induction); because MAF and fuel pressure both read MAP, that single override cascades into the denser
// charge and the higher rail pressure for free. Deliberately simple (algebraic, not time-integrating) - faithful
// enough that related PIDs move together, with real tuning left for later.
public abstract class GasV8Character : IEngineCharacter
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    // Base pressure of the fuel rail: the constant differential a manifold-referenced regulator holds across the
    // injectors. 4 bar = 400 kPa.
    protected const double FuelRailBaseKpa = 400.0;

    // Manifold absolute pressure (kPa) at this operating point - the induction signature of the engine. An NA motor
    // only ever pulls vacuum (ceilings at barometric); a boosted one drives MAP above barometric under load.
    protected abstract double ManifoldPressureKpa(in OperatingPoint op);

    public double Derive(SignalId id, in OperatingPoint op)
    {
        double map = ManifoldPressureKpa(op);
        double baro = op.BaroKpa;
        double ts = op.TimeSeconds;
        bool running = op.Running;

        // Key-on-engine-off is a cold soak: coolant/intake/oil have equalised to ambient, and ambient itself sits at
        // the ISA sea-level standard (15 C). While running they read their warm quasi-static values instead.
        const double IsaSeaLevelTempC = 15.0;
        bool engineOff = op.EngineOff;

        return id switch
        {
            // Temperatures: warm quasi-static when running, cold-soaked to the ISA sea-level standard with the engine off.
            SignalId.CoolantTemp => engineOff ? IsaSeaLevelTempC : 90,
            SignalId.IntakeAirTemp => engineOff ? IsaSeaLevelTempC : 35,
            SignalId.AmbientAirTemp => engineOff ? IsaSeaLevelTempC : 25,
            SignalId.FuelLevel => 57,
            SignalId.EngineOilTemp => engineOff ? IsaSeaLevelTempC : 95,
            SignalId.BarometricPressure => baro,

            // Charge / airflow. The induction curve lives in ManifoldPressureKpa; MAF follows the resulting charge.
            SignalId.ManifoldAbsolutePressure => map,
            SignalId.MassAirFlow => running ? op.Rpm * map * 1.5e-4 : 0,

            // Fuel rail pressure from a manifold-referenced regulator. The regulator's spring side vents to the intake
            // manifold, so it holds a constant differential (the 4 bar base) ACROSS the injectors; the gauge pressure a
            // rail sensor reads is therefore the base minus manifold vacuum, i.e. base + (MAP - baro). It sags at idle
            // (deep vacuum), climbs to base at WOT on an NA motor, and rises ABOVE base under boost - and, because MAP
            // sits at barometric with the engine off, reads the primed base pressure at key-on-engine-off.
            SignalId.FuelPressure => FuelRailBaseKpa + (map - baro),

            // Spark and electrical. More advance at light load; charging voltage only while running.
            SignalId.TimingAdvance => running ? 32.0 - 0.2 * op.Load : 0,
            SignalId.ControlModuleVoltage => running ? 14.2 : 12.6,

            // Pedal echoes the commanded throttle.
            SignalId.AcceleratorPedalPosition => op.Throttle,

            // Fuelling. Closed loop dithers around stoich; WOT commands rich.
            SignalId.CommandedEquivalenceRatio => op.Wot ? 0.85 : 1.0,
            SignalId.ShortTermFuelTrimBank1 => op.ClosedLoop ? 4.0 * Math.Sin(2 * Math.PI * 0.4 * ts) : 0,
            SignalId.ShortTermFuelTrimBank2 => op.ClosedLoop ? 4.0 * Math.Sin(2 * Math.PI * 0.4 * ts + 0.7) : 0,
            SignalId.LongTermFuelTrimBank1 => 2.0,
            SignalId.LongTermFuelTrimBank2 => 1.5,

            // O2 sensors. Front sensors switch in closed loop (the texture), peg rich at WOT and lean on fuel cut;
            // rear (post-cat) sensors sit high and steady. Inactive when the engine is off.
            SignalId.O2VoltageBank1Sensor1 => FrontO2(running, op.Wot, op.OverrunCut, ts, 0.0),
            SignalId.O2VoltageBank2Sensor1 => FrontO2(running, op.Wot, op.OverrunCut, ts, 1.1),
            SignalId.O2VoltageBank1Sensor2 => running ? 0.65 : 0.45,
            SignalId.O2VoltageBank2Sensor2 => running ? 0.65 : 0.45,

            _ => 0,
        };
    }

    // Front (pre-cat) O2 voltage: rich/lean pegs in open loop, otherwise the 0.1-0.9 V switching that a healthy
    // closed-loop sensor shows. The phase argument decorrelates the two banks so they don't switch in lockstep.
    protected static double FrontO2(bool running, bool wot, bool overrunCut, double ts, double phase)
    {
        if (!running) return 0.45;
        if (wot) return 0.85;
        if (overrunCut) return 0.10;
        return 0.45 + 0.35 * Math.Sin(2 * Math.PI * 1.2 * ts + phase);
    }
}
