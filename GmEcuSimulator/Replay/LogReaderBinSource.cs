using BinaryWorker;
using Core.Replay;

namespace GmEcuSimulator.Replay;

// Adapter from the sibling Gmlan Data Logger's BinaryWorker.LogReader to the
// simulator's IBinSource contract. Lives here (in GmEcuSimulator) rather
// than in Core/Replay because Core deliberately does NOT reference
// BinaryWorker - only the composition root that wires the bin-replay
// feature pulls the sibling DLLs in. That keeps Core build-clean for
// anyone running it without the sibling repo on disk.
//
// LogReader's RowCount / GetElapsed / GetValue have the exact O(1) shape
// IBinSource expects; the only translation work is mapping the sibling's
// Header (Common.Enumerations.eSize / eNodeType / eDataType) to the leaf
// BinChannelHeader record that Core consumes.
public sealed class LogReaderBinSource : IBinSource
{
    private readonly LogReader reader;
    private readonly BinChannelHeader[] headers;

    public LogReaderBinSource(LogReader reader)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        var meta = reader.ChannelMetadata;
        headers = new BinChannelHeader[meta.Length];
        for (int i = 0; i < meta.Length; i++)
        {
            var h = meta[i];
            headers[i] = new BinChannelHeader(
                Uid: h.Uid,
                Name: h.Name ?? "",
                Unit: h.Unit ?? "",
                Address: h.Address,
                SizeBytes: (int)h.Size,
                NodeType: (ushort)h.NodeType,
                DataTypeCode: (int)h.DataType,
                Scalar: h.Scalar,
                OffsetEng: h.Offset);
        }
    }

    public int RowCount => reader.RowCount;
    public int ChannelCount => reader.ChannelCount;
    public long GetElapsedMs(int row) => reader.GetElapsed(row);
    public float GetValue(int channelIndex, int row) => reader.GetValue(channelIndex, row);
    public IReadOnlyList<BinChannelHeader> ChannelHeaders => headers;

    public void Dispose() => reader.Dispose();
}
