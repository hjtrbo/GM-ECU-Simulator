namespace Common.Signals;

// Static description of a signal: how it reads to a human and the plausible engineering range it lives in. The range
// is advisory - it bounds editor sliders and clamps egregious overrides - but the EngineModel's own derivation is
// what normally keeps values sane. Wire scaling deliberately does NOT live here: each mode owns its own encoding
// (J1979 formulas for $01, A2L scalars for $22), so one signal can be dressed differently per mode.
public sealed record SignalDef(SignalId Id, string Name, string Unit, double Min, double Max);

// The built-in signal catalogue. The set is fixed in code because the engine model is universal gas-V8 physics, not
// per-ECU config; callers can therefore rely on every SignalId having exactly one entry here.
public static class SignalCatalogue
{
    private static readonly SignalDef[] AllDefs =
    {
        new(SignalId.EngineRpm,                  "Engine RPM",                  "rpm",    0, 8000),
        new(SignalId.VehicleSpeed,               "Vehicle Speed",               "km/h",   0, 255),
        new(SignalId.ThrottlePosition,           "Throttle Position",           "%",      0, 100),
        new(SignalId.EngineLoad,                 "Engine Load",                 "%",      0, 100),
        new(SignalId.ManifoldAbsolutePressure,   "Manifold Absolute Pressure",  "kPa",    0, 255),
        new(SignalId.MassAirFlow,                "Mass Air Flow",               "g/s",    0, 655),
        new(SignalId.FuelPressure,               "Fuel Pressure",               "kPa",    0, 765),
        new(SignalId.TimingAdvance,              "Timing Advance",              "deg",  -64, 64),
        new(SignalId.ControlModuleVoltage,       "Control Module Voltage",      "V",      0, 18),
        new(SignalId.ShortTermFuelTrimBank1,     "Short Term Fuel Trim B1",     "%",   -100, 100),
        new(SignalId.LongTermFuelTrimBank1,      "Long Term Fuel Trim B1",      "%",   -100, 100),
        new(SignalId.ShortTermFuelTrimBank2,     "Short Term Fuel Trim B2",     "%",   -100, 100),
        new(SignalId.LongTermFuelTrimBank2,      "Long Term Fuel Trim B2",      "%",   -100, 100),
        new(SignalId.O2VoltageBank1Sensor1,      "O2 Voltage B1S1",             "V",      0, 1.275),
        new(SignalId.O2VoltageBank1Sensor2,      "O2 Voltage B1S2",             "V",      0, 1.275),
        new(SignalId.O2VoltageBank2Sensor1,      "O2 Voltage B2S1",             "V",      0, 1.275),
        new(SignalId.O2VoltageBank2Sensor2,      "O2 Voltage B2S2",             "V",      0, 1.275),
        new(SignalId.CommandedEquivalenceRatio,  "Commanded Equivalence Ratio", "lambda", 0, 2),
        new(SignalId.AcceleratorPedalPosition,   "Accelerator Pedal Position",  "%",      0, 100),
        new(SignalId.CoolantTemp,                "Engine Coolant Temp",         "degC", -40, 215),
        new(SignalId.IntakeAirTemp,              "Intake Air Temp",             "degC", -40, 215),
        new(SignalId.BarometricPressure,         "Barometric Pressure",         "kPa",    0, 255),
        new(SignalId.AmbientAirTemp,             "Ambient Air Temp",            "degC", -40, 215),
        new(SignalId.FuelLevel,                  "Fuel Level",                  "%",      0, 100),
        new(SignalId.EngineOilTemp,              "Engine Oil Temp",             "degC", -40, 215),
    };

    private static readonly IReadOnlyDictionary<SignalId, SignalDef> ByIdMap = AllDefs.ToDictionary(d => d.Id);

    public static SignalDef Get(SignalId id) => ByIdMap[id];

    public static IReadOnlyList<SignalDef> All => AllDefs;
}
