using Common.Protocol;
using Common.Signals;

namespace Core.Ecu;

// Gives a freshly-created EcuSimulator ECU a curated set of live $22 (ReadDataByIdentifier, 2-byte DID) PIDs so its
// "$22" editor section isn't empty out of the box and a tester sees scenario-correlated values immediately.
//
// Each row uses a REAL GM DID number + A2L linear scaling pulled from the embedded E38/E67 A2L library, and is backed
// by a signal from the shared engine model - so the value moves with the active scenario (and idle dither) exactly
// like the $01 projection, just dressed in GM A2L scaling instead of the legislated J1979 formula. The wire byte
// width is chosen to comfortably hold the scaled raw (the A2L's own element-size field is the internal fixed-point
// width, not the $22 response length, so it isn't trustworthy here).
//
// Only quantities with a clean integer-encoded DID in the A2L are included; MAF and vehicle speed are intentionally
// omitted (the A2L exposes those only as FLOAT32 / via driver-state bytes, which the integer ValueCodec can't dress
// faithfully). A donor bin / DPS archive still replaces this with the vehicle's real $22 map when one is supplied.
//
// Entry points: the Add ECU button, the first-launch DefaultEcuConfig, AND a re-seed pass after each EcuSimulator
// config load (DefaultEcuConfig.SeedDefaults) so a config saved before this set existed still backfills the set on the
// next launch. Primed ECUs own their $22 set from the archive and are skipped via the IsPrimed guard. Existing rows at
// a DID are never overwritten, so it's precedence-safe against a loaded config and a no-op on re-seed.
public static class EcuMode22Seeder
{
    // DID + signal + A2L scaling (phys = Scalar*raw + Offset) + wire width. DID numbers and slopes are verbatim from
    // the embedded A2L $22 library; widths are sized to fit the scaled raw.
    public readonly record struct Mode22Seed(
        ushort Did, SignalId Signal, string Name, double Scalar, double Offset, int Bytes, PidDataType Type);

    public static readonly Mode22Seed[] Seeds =
    {
        new(0x1421, SignalId.EngineRpm,                  "Engine RPM",                    0.125,       0, 2, PidDataType.Unsigned),
        new(0x0005, SignalId.CoolantTemp,                "Engine coolant temperature",    0.0078125,   0, 2, PidDataType.Signed),
        new(0x000A, SignalId.FuelPressure,               "Estimated fuel rail pressure",  0.03125,     0, 2, PidDataType.Unsigned),
        new(0x000B, SignalId.ManifoldAbsolutePressure,   "Intake manifold abs pressure",  0.00390625,  0, 2, PidDataType.Unsigned),
        new(0x000F, SignalId.IntakeAirTemp,              "Intake air temperature",        0.0078125,   0, 2, PidDataType.Signed),
        new(0x000E, SignalId.TimingAdvance,              "Spark advance",                 0.0078125,   0, 2, PidDataType.Signed),
        new(0x004C, SignalId.ThrottlePosition,           "Throttle position",             0.00152588,  0, 2, PidDataType.Unsigned),
        new(0x0042, SignalId.ControlModuleVoltage,       "Run/crank voltage",             0.000976562, 0, 2, PidDataType.Signed),
        new(0x0044, SignalId.CommandedEquivalenceRatio,  "Commanded equivalence ratio",   0.000976562, 0, 2, PidDataType.Unsigned),
        new(0x0046, SignalId.AmbientAirTemp,             "Estimated ambient air temp",    0.0078125,   0, 2, PidDataType.Signed),
        new(0x002F, SignalId.FuelLevel,                  "Fuel tank level",               0.00305176,  0, 2, PidDataType.Unsigned),
        new(0x0049, SignalId.AcceleratorPedalPosition,   "Accelerator pedal position D",  0.00152588,  0, 2, PidDataType.Unsigned),
        new(0x004A, SignalId.AcceleratorPedalPosition,   "Accelerator pedal position E",  0.00152588,  0, 2, PidDataType.Unsigned),
    };

    // Adds every seed DID the ECU does not already carry as a Mode22 row. Existing rows win (loaded config / prior
    // seed); primed ECUs are skipped entirely.
    public static void Seed(EcuNode node)
    {
        if (node.IsPrimed) return;

        foreach (var s in Seeds)
        {
            // A Mode22 row already present at this DID wins over the synthetic default.
            if (node.GetPidByWireId(s.Did) != null) continue;

            node.AddPid(new Pid
            {
                Mode        = PidMode.Mode22,
                Address     = s.Did,
                Name        = s.Name,
                Signal      = s.Signal,
                Scalar      = s.Scalar,
                Offset      = s.Offset,
                LengthBytes = s.Bytes,
                Size        = s.Bytes switch { 1 => PidSize.Byte, 2 => PidSize.Word, _ => PidSize.DWord },
                DataType    = s.Type,
                Unit        = "",
            });
        }
    }
}
