namespace Common.Signals.Engines;

// The resolved operating point at one instant: the engine-agnostic quantities EngineModel computes (the eased
// primaries, the operating-condition flags, bus time, barometric) before handing off to an engine character for the
// model-specific derivation. Nothing here is induction- or fuelling-specific; the character turns it into
// MAP/MAF/timing/fuel/O2 for its particular engine.
public readonly record struct OperatingPoint(
    double Rpm,
    double Load,
    double Throttle,
    double Speed,
    bool Running,
    bool Wot,
    bool OverrunCut,
    bool ClosedLoop,
    bool EngineOff,
    double BaroKpa,
    double TimeSeconds);

// A pluggable engine "character": the model-specific half of the signal layer. EngineModel owns all the universal
// runtime machinery (easing, the accel/decel sweep, overrides, scenarios, dither, the operating-condition flags, the
// run-time reference); a character owns only how a DERIVED signal is computed from the current operating point - the
// induction curve (MAP), airflow, spark, and fuelling that make a naturally-aspirated V8 read differently from a
// boosted one. Swap the character to swap the engine; EngineCharacterRegistry holds the named set the editor offers.
public interface IEngineCharacter
{
    // Stable id stored in config and used to pick this character from the registry. Mirrors the security-module id
    // convention (lower-kebab, family-descriptive).
    string Id { get; }

    // Human-facing label for the editor dropdown.
    string DisplayName { get; }

    // The engineering value of a derived signal at the given operating point. Primaries (rpm/speed/throttle/load) are
    // NOT routed here - EngineModel serves those directly from the scenario - so a character only answers the derived
    // set. An unrecognised signal returns 0.
    double Derive(SignalId id, in OperatingPoint op);
}
