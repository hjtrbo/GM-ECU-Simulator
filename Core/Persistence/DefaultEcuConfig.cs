using Common.Persistence;
using Common.Protocol;
using Common.Waveforms;
using Core.Bus;

namespace Core.Persistence;

// First-launch fallback. If the user has no saved config at the auto-load
// path, we hydrate the bus with these two ECUs so the simulator has
// something visible. Once the user saves their own config, this class
// stays out of the way.
//
// CAN ID convention: GMW3110's worked examples use the legacy GMLAN pairs
// $241/$641, $242/$642, etc. — that's the spec's pedagogical convention,
// not what's deployed. Real OBD-II-compliant GM vehicles (and the sibling
// DataLogger project's eNodeType values: ECM=$7E8, TCM=$7E9, BCM=$7EA,
// FPCM=$7EB) use the standardised $7E0+/$7E8+ pairs:
//   USDT request  = $7E0 + diag-address
//   USDT response = $7E8 + diag-address  (= request + $08)
//   UUDT response = $5E8 + diag-address  (separate space; per-platform)
// Defaults below match real-world hardware so a fresh launch interoperates
// with the DataLogger and Tech 2 Win out-of-the-box.
public static class DefaultEcuConfig
{
    public static SimulatorConfig Build() => new()
    {
        Version = SimulatorConfig.CurrentVersion,
        Description = "Default ECM + TCM (OBD-II 11-bit CAN IDs) with sin/triangle test waveforms",
        Ecus =
        {
            new EcuDto
            {
                Name = "ECM",
                PhysicalRequestCanId = 0x7E0,
                UsdtResponseCanId = 0x7E8,
                UudtResponseCanId = 0x5E8,
                Identifiers = new List<IdentifierDto>
                {
                    // GMW3110 §8.3 / Table 145. DID $90 = VIN, 17 ASCII bytes.
                    // Placeholder WMI 1G1 = GM USA passenger car; remaining
                    // characters are filler so the value is recognisable as
                    // simulator output rather than a real vehicle.
                    new() { Did = 0x90, Name = "VIN", Ascii = "1G1ZB5ST7HF000000" },
                },
                Pids =
                {
                    new PidDto
                    {
                        Address = 0x1234,
                        Name = "Coolant temperature",
                        Size = PidSize.Word,
                        DataType = PidDataType.Unsigned,
                        Scalar = 0.0625,
                        Offset = -40.0,
                        Unit = "°C",
                        Waveform = new WaveformDto
                        {
                            Shape = WaveformShape.Sin,
                            Amplitude = 50, Offset = 80, FrequencyHz = 0.2, PhaseDeg = 0,
                        },
                    },
                    new PidDto
                    {
                        Address = 0x5678,
                        Name = "Engine RPM",
                        Size = PidSize.Word,
                        DataType = PidDataType.Unsigned,
                        Scalar = 0.25,
                        Offset = 0.0,
                        Unit = "RPM",
                        Waveform = new WaveformDto
                        {
                            Shape = WaveformShape.Sin,
                            Amplitude = 3500, Offset = 4500, FrequencyHz = 0.1, PhaseDeg = 0,
                        },
                    },
                },
            },
            new EcuDto
            {
                Name = "TCM",
                PhysicalRequestCanId = 0x7E2,
                UsdtResponseCanId = 0x7EA,
                UudtResponseCanId = 0x5EA,
                Identifiers = new List<IdentifierDto>
                {
                    new() { Did = 0x90, Name = "VIN", Ascii = "1G1ZB5ST7HF000000" },
                },
                Pids =
                {
                    new PidDto
                    {
                        Address = 0x1100,
                        Name = "Trans temperature",
                        Size = PidSize.Word,
                        DataType = PidDataType.Unsigned,
                        Scalar = 0.1,
                        Offset = -40.0,
                        Unit = "°C",
                        Waveform = new WaveformDto
                        {
                            Shape = WaveformShape.Triangle,
                            Amplitude = 30, Offset = 60, FrequencyHz = 0.05, PhaseDeg = 0,
                        },
                    },
                },
            },
        },
    };

    /// <summary>
    /// One-time apply on first launch. If the bus already has nodes
    /// (e.g. the user loaded a saved config) this is a no-op.
    /// </summary>
    public static void ApplyIfEmpty(VirtualBus bus)
    {
        if (bus.Nodes.Count > 0) return;
        ConfigStore.ApplyTo(Build(), bus);
    }
}
