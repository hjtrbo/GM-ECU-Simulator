namespace Common.Signals;

// The named physical quantities a simulated powertrain ECU exposes. This is the single source of truth for live
// values: every live diagnostic projection ($01 J1979 PIDs, $22 live DIDs, $AA streamed packets) reads one of these
// and applies its own wire encoding on top. Adding a signal here makes it available to all of them at once.
//
// Signals fall into three behavioural groups (see EngineModel): primaries are set directly by the active scenario,
// derived signals are computed from the primaries so they stay mutually consistent, and a few are quasi-static
// (fixed warm-engine values for now). The grouping is an EngineModel concern, not encoded in this enum.
public enum SignalId
{
    // Primaries - set directly by the active scenario.
    EngineRpm,
    VehicleSpeed,
    ThrottlePosition,
    EngineLoad,

    // Derived - computed from the primaries on each sample.
    ManifoldAbsolutePressure,
    MassAirFlow,
    FuelPressure,
    TimingAdvance,
    ControlModuleVoltage,
    ShortTermFuelTrimBank1,
    LongTermFuelTrimBank1,
    ShortTermFuelTrimBank2,
    LongTermFuelTrimBank2,
    O2VoltageBank1Sensor1,
    O2VoltageBank1Sensor2,
    O2VoltageBank2Sensor1,
    O2VoltageBank2Sensor2,
    CommandedEquivalenceRatio,
    AcceleratorPedalPosition,

    // Quasi-static - fixed warm-engine values until the thermal axis lands.
    CoolantTemp,
    IntakeAirTemp,
    BarometricPressure,
    AmbientAirTemp,
    FuelLevel,
    EngineOilTemp,
}
