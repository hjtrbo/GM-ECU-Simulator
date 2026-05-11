namespace Core.Replay;

// Read-only abstraction over a row-indexed binary log. The simulator
// consumes only this surface (RowCount + GetElapsedMs(row) + GetValue(ch,row)
// + ChannelHeaders), which lets the coordinator be unit-tested with an
// in-memory FakeBinSource and lets the production adapter wrapping
// BinaryWorker.LogReader land independently of the rest of the feature.
//
// Implementations must be safe for concurrent reads — Sample() is called
// from the DPID scheduler thread, the IPC pipe thread, and the WPF UI
// thread simultaneously. They must NOT mutate after construction (the
// coordinator never mutates and parks old sources for late disposal so
// any in-flight reader finishes before Dispose runs).
public interface IBinSource : IDisposable
{
    int RowCount { get; }
    int ChannelCount { get; }
    long GetElapsedMs(int row);
    float GetValue(int channelIndex, int row);
    IReadOnlyList<BinChannelHeader> ChannelHeaders { get; }
}

// Mirror of BinaryWorker.Header from the sibling Gmlan Data Logger project.
// Kept as a plain record so Common/Core never has to reference the sibling
// directly — adapters translate between this and the sibling's Header.
//
// NodeType values are the eNodeType enum from the sibling, whose numeric
// values are the USDT response CAN IDs (0x7E8 = ECM, 0x7E9 = TCM, 0x7EA =
// BCM, 0x7EB = FPCM). 0 = None — channels with NodeType==0 are unmapped
// and the coordinator skips them on load.
//
// DataTypeCode mirrors the sibling's eDataType: 1=Bool, 2=Unsigned, 3=Signed,
// 4=Hex, 5=Ascii. The coordinator maps 3=Signed to PidDataType.Signed;
// everything else falls through to PidDataType.Unsigned.
public sealed record BinChannelHeader(
    uint Uid,
    string Name,
    string Unit,
    uint Address,
    int SizeBytes,
    ushort NodeType,
    int DataTypeCode,
    double Scalar,
    double OffsetEng);
