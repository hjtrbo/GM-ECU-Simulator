using Common.Dbc;
using Common.Protocol;
using Common.Signals;
using Core.Ecu;

namespace Core.Dbc;

// Turns a parsed DBC into the simulator's runtime broadcast model, scoped to one transmitter and an
// explicit message pick (a DBC describes the whole bus; an ECU broadcasts only its own module's
// messages). Also auto-maps signals to live engine signals by name/unit heuristic and merges a
// re-import into an existing set.
//
// Lives in Core (not Common) because it produces Core.Ecu.BroadcastMessage; the parser + codec it
// builds on stay in Common.Dbc.
public static class DbcImporter
{
    // Default transmit period when the DBC carries no GenMsgCycleTime for a message.
    public const int DefaultPeriodMs = 100;

    // Transmitter nodes that send at least one message, most-prolific first - the import picker's
    // dropdown order (ECM_HS sends the bulk on a GM HS bus, so it floats to the top).
    public static IReadOnlyList<(string Transmitter, int Count)> TransmittersByMessageCount(DbcDatabase db)
        => db.Messages
            .GroupBy(m => m.Transmitter)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(t => t.Item2)
            .ThenBy(t => t.Key, StringComparer.Ordinal)
            .ToList();

    // Convert the chosen messages (by raw CAN id) of one transmitter into broadcast messages, each
    // signal seeded with its auto-mapped live source (unmapped -> None/0, editable afterward).
    public static List<BroadcastMessage> ToBroadcasts(DbcDatabase db, string transmitter, IReadOnlySet<uint> selectedIds)
        => db.Messages
            .Where(m => m.Transmitter == transmitter && selectedIds.Contains(m.Id))
            .Select(ToBroadcast)
            .ToList();

    public static BroadcastMessage ToBroadcast(DbcMessage m)
    {
        var msg = new BroadcastMessage
        {
            CanId = m.Id,
            Extended = m.Extended,
            Name = m.Name,
            Dlc = m.Dlc,
            PeriodMs = m.CycleTimeMs is > 0 ? m.CycleTimeMs!.Value : DefaultPeriodMs,
            Enabled = true,
        };
        foreach (var s in m.Signals)
        {
            var mapped = AutoMap(s);
            msg.Signals.Add(new BroadcastSignal
            {
                Name = s.Name,
                StartBit = s.StartBit,
                Length = s.Length,
                ByteOrder = s.ByteOrder,
                Signed = s.Signed,
                Scale = s.Scale,
                Offset = s.Offset,
                Unit = s.Unit,
                Min = s.Min,
                Max = s.Max,
                Signal = mapped,
                ValueSource = mapped.HasValue ? BroadcastValueSource.Signal : BroadcastValueSource.None,
            });
        }
        return msg;
    }

    // True when the message carries at least one signal we can drive from the live engine model -
    // the picker uses this to pre-tick "interesting" messages.
    public static bool HasMappableSignal(DbcMessage m) => m.Signals.Any(s => AutoMap(s) is not null);

    // Best-effort name/unit heuristic mapping a DBC signal to a live engine SignalId. Conservative:
    // rate-of-change / input / output / turbine variants are deliberately left unmapped so they don't
    // masquerade as the primary signal. Returns null when nothing fits (-> a constant-0 field).
    public static SignalId? AutoMap(DbcSignal s)
    {
        string n = s.Name.ToLowerInvariant();
        string u = s.Unit.ToLowerInvariant();

        // Reject derivative / secondary speed variants up front.
        bool rate = n.Contains("roc") || n.Contains("rate") || n.Contains("_dt") || u.Contains("/s");

        if (!rate && u == "rpm" && Has(n, "engine") && Has(n, "speed")
            && !Has(n, "input") && !Has(n, "output") && !Has(n, "turbine") && !Has(n, "trans"))
            return SignalId.EngineRpm;

        if (!rate && (Has(n, "vehicle", "speed") || n.Contains("vskph") || n.Contains("vss") || u == "km/h" || u == "kph"))
            return SignalId.VehicleSpeed;

        if (Has(n, "coolant") || n.Contains("ect"))
            return SignalId.CoolantTemp;

        if (Has(n, "intake", "temp") || n.Contains("iat"))
            return SignalId.IntakeAirTemp;

        if (Has(n, "oil", "temp") || n.Contains("eot"))
            return SignalId.EngineOilTemp;

        if (!rate && Has(n, "throttle", "position") && !n.Contains("pedal"))
            return SignalId.ThrottlePosition;

        if (Has(n, "pedal") && (u == "%" || u.Length == 0))
            return SignalId.AcceleratorPedalPosition;

        if (n.Contains("maf") || Has(n, "mass", "air"))
            return SignalId.MassAirFlow;

        if (n.Contains("manifold") || (n.Contains("map") && u.Contains("kpa")))
            return SignalId.ManifoldAbsolutePressure;

        if (n.Contains("baro"))
            return SignalId.BarometricPressure;

        if (Has(n, "engine", "load") || n == "load")
            return SignalId.EngineLoad;

        if (n.Contains("vbat") || Has(n, "battery", "volt") || (u == "v" && n.Contains("volt")))
            return SignalId.ControlModuleVoltage;

        if (Has(n, "fuel", "level"))
            return SignalId.FuelLevel;

        if (Has(n, "timing", "advance") || Has(n, "spark", "advance"))
            return SignalId.TimingAdvance;

        return null;
    }

    private static bool Has(string haystack, params string[] tokens)
    {
        foreach (var t in tokens) if (!haystack.Contains(t)) return false;
        return true;
    }
}
