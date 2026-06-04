using Common.Protocol;

namespace Common.Signals;

// Writes exactly the PID's data bytes (big-endian) into dest, given the live engine model + discrete state at bus
// time t. dest is always sized to the PID's declared Length.
public delegate void J1979Encode(EngineModel engine, DiscreteState state, double timeMs, Span<byte> dest);

// Decodes a PID's wire bytes back to a human-readable engineering value, the inverse of Encode. For compound PIDs
// (e.g. an O2 sensor: byte A = sensor voltage, byte B = short-term fuel trim) it folds every sub-field into one
// string - so a single Decoded column handles "nested" PIDs the same way a scan tool's readout would.
public delegate string J1979Decode(ReadOnlySpan<byte> data);

// One OBD-II / SAE J1979 Mode $01 PID: its wire length, how it encodes (the LEGISLATED formula - the whole reason
// Mode $01 can't reuse GM A2L scaling), and how it decodes back to a readable value.
public sealed record J1979Pid(byte Pid, int Length, string Name, J1979Encode Encode, J1979Decode Decode);

// The built-in J1979 Mode $01 catalogue. It defines which data/status PIDs exist and how each encodes/decodes; the
// support-bitmask PIDs ($00/$20/...) are NOT entries here - they are computed from whichever subset an ECU enables
// (see ComputeSupportMask / IsBitmaskPid).
public static class J1979Catalogue
{
    private const double TrimScalar = 100.0 / 128.0;    // fuel trim: eng% = (raw - 128) * 100/128
    private const double PercentScalar = 100.0 / 255.0; // 0-100% spread across a full byte

    // Analog PID factory: builds the encoder (signal -> wire via the legislated linear scale) AND the matching decoder
    // (wire -> "value unit") from one set of coefficients, so the two can never drift apart. scalar/offset are the
    // DECODE coefficients (eng = raw*scalar + offset); ValueCodec inverts them on encode.
    private static J1979Pid AnalogPid(byte pid, int length, string name, SignalId sig,
                                      double scalar, double offset, string unit,
                                      PidDataType type = PidDataType.Unsigned)
    {
        J1979Encode enc = (engine, _, t, dest) =>
            ValueCodec.Encode(engine.Sample(sig, t), scalar, offset, type, dest.Length, dest);

        J1979Decode dec = data =>
        {
            double eng = ReadRaw(data, type) * scalar + offset;
            return unit.Length == 0 ? eng.ToString("0.###") : $"{eng:0.###} {unit}";
        };

        return new J1979Pid(pid, length, name, enc, dec);
    }

    // E38/E67-realistic gas-V8 selection spanning three support blocks ($00/$20/$40) so the bitmask cascade is real.
    private static readonly J1979Pid[] Defs =
    {
        // $00-$1F
        new(0x01, 4, "Monitor status since DTCs cleared", EncodeMonitorStatusSinceCleared, DecodeMonitorStatus),
        new(0x03, 2, "Fuel system status",                EncodeFuelSystemStatus,          DecodeFuelSystemStatus),
        AnalogPid(0x04, 1, "Calculated engine load",            SignalId.EngineLoad, PercentScalar, 0, "%"),
        AnalogPid(0x05, 1, "Engine coolant temperature",        SignalId.CoolantTemp, 1, -40, "degC"),
        AnalogPid(0x06, 1, "Short term fuel trim B1",           SignalId.ShortTermFuelTrimBank1, TrimScalar, -100, "%"),
        AnalogPid(0x07, 1, "Long term fuel trim B1",            SignalId.LongTermFuelTrimBank1, TrimScalar, -100, "%"),
        AnalogPid(0x08, 1, "Short term fuel trim B2",           SignalId.ShortTermFuelTrimBank2, TrimScalar, -100, "%"),
        AnalogPid(0x09, 1, "Long term fuel trim B2",            SignalId.LongTermFuelTrimBank2, TrimScalar, -100, "%"),
        AnalogPid(0x0A, 1, "Fuel pressure",                     SignalId.FuelPressure, 3, 0, "kPa"),
        AnalogPid(0x0B, 1, "Intake manifold absolute pressure", SignalId.ManifoldAbsolutePressure, 1, 0, "kPa"),
        AnalogPid(0x0C, 2, "Engine RPM",                        SignalId.EngineRpm, 0.25, 0, "rpm"),
        AnalogPid(0x0D, 1, "Vehicle speed",                     SignalId.VehicleSpeed, 1, 0, "km/h"),
        AnalogPid(0x0E, 1, "Timing advance",                    SignalId.TimingAdvance, 0.5, -64, "deg"),
        AnalogPid(0x0F, 1, "Intake air temperature",            SignalId.IntakeAirTemp, 1, -40, "degC"),
        AnalogPid(0x10, 2, "Mass air flow",                     SignalId.MassAirFlow, 0.01, 0, "g/s"),
        AnalogPid(0x11, 1, "Throttle position",                 SignalId.ThrottlePosition, PercentScalar, 0, "%"),
        new(0x13, 1, "O2 sensors present (2 banks)", (_, s, _, d) => d[0] = s.O2SensorsPresent, DecodeO2Present),
        new(0x14, 2, "O2 sensor B1S1", EncodeO2(SignalId.O2VoltageBank1Sensor1, SignalId.ShortTermFuelTrimBank1), DecodeO2),
        new(0x18, 2, "O2 sensor B2S1", EncodeO2(SignalId.O2VoltageBank2Sensor1, SignalId.ShortTermFuelTrimBank2), DecodeO2),
        new(0x1C, 1, "OBD standards conformance", (_, s, _, d) => d[0] = s.ObdStandard, DecodeObdStandard),
        new(0x1F, 2, "Run time since engine start", EncodeRunTime, data => $"{ReadU16(data)} s"),
        // $20-$3F
        new(0x21, 2, "Distance with MIL on", (_, s, _, d) => WriteU16(d, (ushort)(s.MilOn ? 12 : 0)), data => $"{ReadU16(data)} km"),
        AnalogPid(0x2F, 1, "Fuel tank level", SignalId.FuelLevel, PercentScalar, 0, "%"),
        new(0x30, 1, "Warm-ups since codes cleared", (_, _, _, d) => d[0] = 40, data => data[0].ToString()),
        new(0x31, 2, "Distance since codes cleared", (_, _, _, d) => WriteU16(d, 1234), data => $"{ReadU16(data)} km"),
        AnalogPid(0x33, 1, "Barometric pressure", SignalId.BarometricPressure, 1, 0, "kPa"),
        // $40-$5F
        new(0x41, 4, "Monitor status this drive cycle", EncodeMonitorStatusThisCycle, _ => "monitors complete"),
        AnalogPid(0x42, 2, "Control module voltage", SignalId.ControlModuleVoltage, 0.001, 0, "V"),
        AnalogPid(0x44, 2, "Commanded equivalence ratio", SignalId.CommandedEquivalenceRatio, 1.0 / 32768.0, 0, "lambda"),
        AnalogPid(0x45, 1, "Relative throttle position", SignalId.ThrottlePosition, PercentScalar, 0, "%"),
        AnalogPid(0x46, 1, "Ambient air temperature", SignalId.AmbientAirTemp, 1, -40, "degC"),
        // Both legislated pedal-sensor PIDs read the one Accelerator Pedal Position signal (the two redundant channels
        // track together on a healthy throttle-by-wire pedal).
        AnalogPid(0x49, 1, "Accelerator pedal position D", SignalId.AcceleratorPedalPosition, PercentScalar, 0, "%"),
        AnalogPid(0x4A, 1, "Accelerator pedal position E", SignalId.AcceleratorPedalPosition, PercentScalar, 0, "%"),
        new(0x51, 1, "Fuel type", (_, s, _, d) => d[0] = s.FuelType, DecodeFuelType),
        AnalogPid(0x5C, 1, "Engine oil temperature", SignalId.EngineOilTemp, 1, -40, "degC"),
    };

    private static readonly IReadOnlyDictionary<byte, J1979Pid> ByPid = Defs.ToDictionary(p => p.Pid);

    // Every data/status PID the catalogue can answer. A fresh ECU's default supported subset is exactly this set.
    public static IReadOnlySet<byte> DefaultSupported { get; } = Defs.Select(p => p.Pid).ToHashSet();

    // Every data/status PID the catalogue defines, in id order. The editor's whole-ECU view lists these as the $01
    // rows (read-only encoding; the per-row Supported toggle drives whether each is advertised).
    public static IReadOnlyList<J1979Pid> All => Defs;

    // Definition for a data/status PID, or null if this PID id is not a catalogue entry (which includes the bitmask
    // PIDs - those are computed, not encoded from a definition).
    public static J1979Pid? Get(byte pid) => ByPid.TryGetValue(pid, out var d) ? d : null;

    // The support-list PIDs ($00, $20, $40, ... $E0) are structural rather than data: a tool reads them to discover
    // the PID map. They are computed from the supported subset, never stored, so the advertised map cannot drift out
    // of sync with what the ECU actually answers.
    public static bool IsBitmaskPid(byte pid) => pid % 0x20 == 0;

    // Whether the ECU answers a given bitmask PID. $00 is the always-present entry point; a higher block ($20/$40/...)
    // is answered only when some supported PID lives beyond it, so a tool stops walking exactly where the map ends.
    public static bool BitmaskAnswerable(byte pid, IReadOnlySet<byte> supported)
        => pid == 0x00 ? supported.Count > 0 : supported.Any(q => q > pid);

    // Fill dest (4 bytes) with the support bitmask for the block beginning at basePid (covers basePid+1 .. +0x20).
    // The MSB of byte 0 is the lowest PID in the block; block-boundary PIDs are flagged so tools chain onward.
    public static void ComputeSupportMask(byte basePid, IReadOnlySet<byte> supported, Span<byte> dest)
    {
        uint mask = 0;
        for (int k = 0; k < 32; k++)
        {
            byte pid = (byte)(basePid + 1 + k);
            if (IsPidSupported(pid, supported))
                mask |= 1u << (31 - k);
        }
        WriteU32(dest, mask);
    }

    private static bool IsPidSupported(byte pid, IReadOnlySet<byte> supported)
    {
        if (supported.Contains(pid)) return true;
        // A block-boundary PID ($20/$40/...) advertises itself whenever there is anything to chain to past it.
        return IsBitmaskPid(pid) && supported.Any(q => q > pid);
    }

    // PID $01: lamp + stored-DTC count, then the readiness-monitor bytes. Warm-only means every supported monitor
    // reports complete (the "incomplete" bits stay clear); the supported set is a representative spark-ignition
    // selection and is the obvious thing to make configurable later.
    private static void EncodeMonitorStatusSinceCleared(EngineModel engine, DiscreteState s, double t, Span<byte> d)
    {
        d[0] = (byte)((s.MilOn ? 0x80 : 0x00) | (s.StoredDtcCount & 0x7F));
        d[1] = 0x07; // misfire + fuel + comprehensive supported; bit3 clear = spark ignition; incomplete bits clear
        d[2] = 0x65; // representative spark monitors supported: catalyst, evap, O2 sensor, O2 heater
        d[3] = 0x00; // none incomplete
    }

    // PID $41 mirrors $01's readiness layout for the current drive cycle; byte A's MIL/DTC field is not used here.
    private static void EncodeMonitorStatusThisCycle(EngineModel engine, DiscreteState s, double t, Span<byte> d)
    {
        d[0] = 0x00;
        d[1] = 0x07;
        d[2] = 0x65;
        d[3] = 0x00;
    }

    // PID $03: closed loop (0x02) once the engine is fuelling on the sensors; open loop due to driving conditions
    // (0x04) at WOT / overrun fuel cut; open loop not-yet-ready (0x01) when the engine is not running. One fuel
    // system reported (byte B = 0).
    private static void EncodeFuelSystemStatus(EngineModel engine, DiscreteState s, double t, Span<byte> d)
    {
        bool running = engine.Sample(SignalId.EngineRpm, t) > 50;
        d[0] = engine.IsClosedLoop(t) ? (byte)0x02 : running ? (byte)0x04 : (byte)0x01;
        d[1] = 0x00;
    }

    // PID $14/$18 etc: byte A = sensor voltage at 0.005 V/bit, byte B = the associated short-term fuel trim.
    private static J1979Encode EncodeO2(SignalId voltage, SignalId trim) => (engine, _, t, d) =>
    {
        d[0] = (byte)Math.Clamp((int)Math.Round(engine.Sample(voltage, t) * 200.0), 0, 255);
        d[1] = (byte)Math.Clamp((int)Math.Round((engine.Sample(trim, t) + 100.0) * 1.28), 0, 255);
    };

    // PID $1F: run time since engine start, in seconds. Reads zero with the engine off (KOEO) and counts up from the
    // moment the engine cranks; see EngineModel.RunTimeSecondsSinceStart.
    private static void EncodeRunTime(EngineModel engine, DiscreteState s, double t, Span<byte> d)
        => WriteU16(d, (ushort)Math.Clamp((int)engine.RunTimeSecondsSinceStart(t), 0, ushort.MaxValue));

    // ---------------- decoders for the status / compound (nested-byte) PIDs ----------------

    private static string DecodeMonitorStatus(ReadOnlySpan<byte> d)
    {
        bool mil = (d[0] & 0x80) != 0;
        int dtc = d[0] & 0x7F;
        return $"MIL {(mil ? "on" : "off")}, {dtc} DTC(s)";
    }

    private static string DecodeFuelSystemStatus(ReadOnlySpan<byte> d) => d[0] switch
    {
        0x01 => "open loop (warming up)",
        0x02 => "closed loop",
        0x04 => "open loop (load/decel)",
        0x08 => "open loop (fault)",
        0x10 => "closed loop (fault)",
        0x00 => "n/a",
        _    => $"0x{d[0]:X2}",
    };

    // Nested PID: byte A = sensor voltage (0.005 V/bit), byte B = short-term fuel trim ((raw-128)*100/128); 0xFF = the
    // trim half is unused. Both fields fold into one string for the Decoded column.
    private static string DecodeO2(ReadOnlySpan<byte> d)
    {
        double volt = d[0] * 0.005;
        if (d[1] == 0xFF) return $"{volt:0.###} V";
        double trim = (d[1] - 128) * 100.0 / 128.0;
        return $"{volt:0.###} V, STFT {trim:+0.#;-0.#;0}%";
    }

    // PID $13 bitmap: bits 0-3 = bank 1 sensors 1-4, bits 4-7 = bank 2 sensors 1-4.
    private static string DecodeO2Present(ReadOnlySpan<byte> d)
    {
        byte b = d[0];
        var present = new List<string>();
        for (int bank = 1; bank <= 2; bank++)
            for (int sensor = 1; sensor <= 4; sensor++)
                if ((b & (1 << ((bank - 1) * 4 + (sensor - 1)))) != 0)
                    present.Add($"B{bank}S{sensor}");
        return present.Count == 0 ? "none" : string.Join(",", present);
    }

    private static string DecodeObdStandard(ReadOnlySpan<byte> d) => d[0] switch
    {
        0x01 => "OBD-II (CARB)",
        0x03 => "OBD and OBD-II",
        0x06 => "EOBD",
        _    => $"standard 0x{d[0]:X2}",
    };

    private static string DecodeFuelType(ReadOnlySpan<byte> d) => d[0] switch
    {
        0x01 => "Gasoline",
        0x04 => "Diesel",
        0x08 => "Electric",
        _    => $"type 0x{d[0]:X2}",
    };

    // Big-endian raw integer from the wire bytes, sign-extended for Signed PIDs.
    private static long ReadRaw(ReadOnlySpan<byte> d, PidDataType type)
    {
        ulong u = 0;
        foreach (var b in d) u = (u << 8) | b;
        if (type == PidDataType.Signed && d.Length is >= 1 and <= 4)
        {
            int bits = 8 * d.Length;
            if ((u & (1UL << (bits - 1))) != 0) return (long)u - (1L << bits);
        }
        return (long)u;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> d) => (ushort)((d[0] << 8) | d[1]);

    private static void WriteU16(Span<byte> d, ushort v)
    {
        d[0] = (byte)(v >> 8);
        d[1] = (byte)v;
    }

    private static void WriteU32(Span<byte> d, uint v)
    {
        d[0] = (byte)(v >> 24);
        d[1] = (byte)(v >> 16);
        d[2] = (byte)(v >> 8);
        d[3] = (byte)v;
    }
}
