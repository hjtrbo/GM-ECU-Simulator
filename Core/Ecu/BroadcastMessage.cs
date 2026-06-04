using Common.Dbc;
using Common.Protocol;
using Common.Signals;

namespace Core.Ecu;

// Runtime model for one unsolicited CAN broadcast message (the DBC-driven "background traffic" a
// passive logger sees). All fields are mutable so the editor can tweak them live; the scheduler
// reads them on each tick. A message owns an ordered set of bit-packed signals it composes into a
// DLC-byte payload.
//
// Distinct from the diagnostic response path: CanId is a raw application arbitration ID, not a
// $7E8-style response id, and emission is timer-driven, not request/response. See
// Core.Scheduler.BroadcastScheduler for the emit loop and Common.Dbc.CanSignalCodec for packing.
public sealed class BroadcastMessage
{
    public uint CanId { get; set; }
    public bool Extended { get; set; }
    public string Name { get; set; } = "";
    public int Dlc { get; set; } = 8;
    public int PeriodMs { get; set; } = 100;
    public bool Enabled { get; set; } = true;
    public List<BroadcastSignal> Signals { get; } = new();

    // Compose this message's payload at the given bus time by packing every signal's current value.
    // Each signal samples its source (live engine signal / constant / none) and writes its bit field;
    // unmapped bits stay 0. The buffer is exactly Dlc bytes (clamped to 0..64 for safety).
    public byte[] BuildPayload(EngineModel engine, double timeMs)
    {
        int dlc = Dlc < 0 ? 0 : Dlc > 64 ? 64 : Dlc;
        var buf = new byte[dlc];
        foreach (var sig in Signals)
            sig.Pack(buf, engine, timeMs);
        return buf;
    }
}

// One bit-packed field within a broadcast message. The layout block matches the DBC SG_ definition;
// the source block decides where the value comes from at emit time.
public sealed class BroadcastSignal
{
    public string Name { get; set; } = "";

    // DBC layout
    public int StartBit { get; set; }
    public int Length { get; set; }
    public DbcByteOrder ByteOrder { get; set; } = DbcByteOrder.Motorola;
    public bool Signed { get; set; }
    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; }
    public string Unit { get; set; } = "";
    public double Min { get; set; }
    public double Max { get; set; }

    // Value source
    public BroadcastValueSource ValueSource { get; set; } = BroadcastValueSource.None;
    public SignalId? Signal { get; set; }
    public double Constant { get; set; }

    // The current engineering value this field would emit: live engine sample when signal-backed, the
    // fixed constant when constant-sourced, otherwise 0. Used both by Pack and the editor's readout.
    public double SampleValue(EngineModel engine, double timeMs) => ValueSource switch
    {
        BroadcastValueSource.Signal when Signal is { } s => engine.Sample(s, timeMs),
        BroadcastValueSource.Constant => Constant,
        _ => 0.0,
    };

    // Pack this field's current value into the message payload via the DBC bit layout.
    public void Pack(Span<byte> payload, EngineModel engine, double timeMs)
        => CanSignalCodec.Pack(payload, StartBit, Length, ByteOrder, Signed, Scale, Offset,
                               SampleValue(engine, timeMs));
}
