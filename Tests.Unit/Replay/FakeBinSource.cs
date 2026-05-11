using Core.Replay;

namespace EcuSimulator.Tests.Replay;

// Controllable IBinSource for unit tests. Caller supplies elapsed times,
// channel headers, and a per-(channel,row) value provider. RowCount is
// derived from the elapsed array.
internal sealed class FakeBinSource : IBinSource
{
    private readonly long[] elapsed;
    private readonly BinChannelHeader[] headers;
    private readonly float[][] columns;

    public FakeBinSource(long[] elapsed, BinChannelHeader[] headers, Func<int, int, float>? valueOf = null)
    {
        this.elapsed = elapsed;
        this.headers = headers;
        columns = new float[headers.Length][];
        valueOf ??= (ch, row) => ch * 1000 + row;
        for (int c = 0; c < headers.Length; c++)
        {
            columns[c] = new float[elapsed.Length];
            for (int r = 0; r < elapsed.Length; r++)
                columns[c][r] = valueOf(c, r);
        }
    }

    public int RowCount => elapsed.Length;
    public int ChannelCount => headers.Length;
    public long GetElapsedMs(int row) => elapsed[row];
    public float GetValue(int channelIndex, int row) => columns[channelIndex][row];
    public IReadOnlyList<BinChannelHeader> ChannelHeaders => headers;

    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;

    // Convenience: 100 rows at 10 ms intervals, two channels with values
    // 100*ch + row.
    public static FakeBinSource Default()
    {
        var elapsed = new long[100];
        for (int i = 0; i < 100; i++) elapsed[i] = i * 10;
        var headers = new[]
        {
            new BinChannelHeader(1, "RPM", "RPM", 0x000C, 2, 0x7E8, 2, 1.0, 0),
            new BinChannelHeader(2, "MAP", "kPa", 0x000B, 1, 0x7E8, 2, 1.0, 0),
        };
        return new FakeBinSource(elapsed, headers);
    }
}
