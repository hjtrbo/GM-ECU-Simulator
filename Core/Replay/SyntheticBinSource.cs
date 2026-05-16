namespace Core.Replay;

// Development-only IBinSource that generates a deterministic in-memory log
// without touching disk or referencing the sibling BinaryWorker. Lets the
// UI and the start/stop plumbing be exercised end-to-end alongside the
// LogReader-backed adapter that handles real .bin files.
//
// Channel layout (channelIndex -> ECU NodeType, Address, waveform):
//   0  ECM 0x000C (RPM)        sin sweep   500..3500 RPM,  0.5 Hz
//   1  ECM 0x0011 (TPS)        sawtooth     0..100 %,      0.2 Hz
//   2  ECM 0x0005 (Coolant)    triangle    -10..110 °C,    0.05 Hz
//   3  TCM 0x000F (Trans temp) sin          50..120 °C,    0.05 Hz
//
// Sample rate 50 Hz; default duration 60 s. Samples are float to match
// BinaryWorker's on-disk format.
public sealed class SyntheticBinSource : IBinSource
{
    private readonly long[] elapsedMs;
    private readonly float[][] columns;
    private readonly BinChannelHeader[] headers;

    public SyntheticBinSource(int durationSec = 60, int sampleRateHz = 50)
    {
        int rows = durationSec * sampleRateHz;
        elapsedMs = new long[rows];
        for (int i = 0; i < rows; i++) elapsedMs[i] = (long)((i * 1000.0) / sampleRateHz);

        headers = new[]
        {
            new BinChannelHeader(Uid: 1, Name: "RPM",        Unit: "RPM",
                                 Address: 0x000C, SizeBytes: 2, NodeType: 0x7E8,
                                 DataTypeCode: 2, Scalar: 0.25, OffsetEng: 0),
            new BinChannelHeader(Uid: 2, Name: "TPS",        Unit: "%",
                                 Address: 0x0011, SizeBytes: 1, NodeType: 0x7E8,
                                 DataTypeCode: 2, Scalar: 0.392157, OffsetEng: 0),
            new BinChannelHeader(Uid: 3, Name: "Coolant",    Unit: "°C",
                                 Address: 0x0005, SizeBytes: 1, NodeType: 0x7E8,
                                 DataTypeCode: 2, Scalar: 1.0, OffsetEng: -40),
            new BinChannelHeader(Uid: 4, Name: "TransTemp",  Unit: "°C",
                                 Address: 0x000F, SizeBytes: 1, NodeType: 0x7E9,
                                 DataTypeCode: 2, Scalar: 1.0, OffsetEng: -40),
        };

        columns = new float[headers.Length][];
        for (int c = 0; c < headers.Length; c++) columns[c] = new float[rows];

        for (int i = 0; i < rows; i++)
        {
            double t = i / (double)sampleRateHz;
            columns[0][i] = (float)(2000 + 1500 * Math.Sin(2 * Math.PI * 0.5 * t));
            columns[1][i] = (float)((50 + 50 * (((t * 0.2) % 1.0))));
            columns[2][i] = (float)(50 + 60 * Math.Abs(((t * 0.05) % 1.0) * 2 - 1) - 30);
            columns[3][i] = (float)(85 + 35 * Math.Sin(2 * Math.PI * 0.05 * t));
        }
    }

    public int RowCount => elapsedMs.Length;
    public int ChannelCount => headers.Length;
    public long GetElapsedMs(int row) => elapsedMs[row];
    public float GetValue(int channelIndex, int row) => columns[channelIndex][row];
    public IReadOnlyList<BinChannelHeader> ChannelHeaders => headers;

    public void Dispose() { /* nothing to release */ }
}
