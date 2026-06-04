namespace Common.Dbc;

// Parsed representation of a DBC (CAN database) file - just the constructs the broadcast feature
// needs (messages, signals, cycle times). VAL_TABLE_ / VAL_ / CM_ / BA_DEF_ / BU_ etc. are
// tolerated-and-skipped by the parser, so this model deliberately omits them.
//
// A DBC describes the WHOLE bus (every module's transmit set). The simulator only broadcasts what
// one ECU sends, so import is always scoped by transmitter + an explicit message pick (see
// Core.Dbc.DbcImporter); this model carries every message verbatim and lets the importer filter.

// Bit/byte order of a signal field within the CAN payload. DBC encodes this as the digit after the
// '@' in an SG_ line: @0 = Motorola (big-endian, MSB-first sawtooth start bit), @1 = Intel
// (little-endian, LSB-first). See CanSignalCodec for the exact bit placement.
public enum DbcByteOrder
{
    Motorola = 0,
    Intel = 1,
}

public sealed class DbcDatabase
{
    public List<DbcMessage> Messages { get; } = new();
}

// One DBC message: an arbitration ID + DLC carrying a set of bit-packed signals, optionally with a
// transmit cycle time (BA_ "GenMsgCycleTime").
public sealed class DbcMessage
{
    public uint Id { get; init; }                       // 11- or 29-bit arbitration ID (extended bit masked off)
    public bool Extended { get; init; }                 // true when the DBC id had the 0x80000000 extended flag set
    public string Name { get; init; } = "";
    public int Dlc { get; init; }                       // payload length in bytes (0..8 for classical CAN)
    public string Transmitter { get; init; } = "";      // the BU_ node name that sends this message
    public int? CycleTimeMs { get; set; }               // from BA_ "GenMsgCycleTime"; null when the DBC omits it
    public List<DbcSignal> Signals { get; } = new();
}

// One signal field within a message. Layout fields come straight from the SG_ line; Scale/Offset map
// the raw integer to engineering units (physical = raw * Scale + Offset), which the simulator inverts
// when packing a live value.
public sealed class DbcSignal
{
    public string Name { get; init; } = "";
    public int StartBit { get; init; }                  // DBC start-bit numbering (Motorola = MSB sawtooth, Intel = LSB)
    public int Length { get; init; }                    // field width in bits
    public DbcByteOrder ByteOrder { get; init; }
    public bool Signed { get; init; }                   // '-' = signed two's complement, '+' = unsigned
    public double Scale { get; init; } = 1.0;
    public double Offset { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public string Unit { get; init; } = "";

    // Multiplexing: 'M' marks the multiplexor switch signal; 'm<n>' marks a signal that is only
    // present when the switch equals <n>. v1 emits only the multiplexor's default frame (see plan
    // limitations), but the metadata is parsed and persisted for later.
    public bool IsMultiplexor { get; init; }
    public int? MultiplexedOn { get; init; }
}
