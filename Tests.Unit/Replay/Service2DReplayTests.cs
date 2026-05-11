using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Replay;
using Core.Services;
using EcuSimulator.Tests.Core;

namespace EcuSimulator.Tests.Replay;

// Regression: a $2D DefinePidByAddress that aliases a bin-replay PID's
// memory address must inherit the replay-waveform factory from the source
// Pid, not just the WaveformConfig. Without that propagation the cloned
// Pid resolves Shape=FileStream to ConstantWaveform(0) and the host sees
// 0x0000 on the wire (manifested as $22 / $AA returning all-zero values
// after a successful $2D alias of a bin-loaded address).
public class Service2DReplayTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    [Fact]
    public void Define_AliasOfBinReplayAddress_ReturnsBinValueOnRead()
    {
        // Replicates the field trace that surfaced this bug: host sends a $2D
        // with a 2-byte memory address (0x2000), aliasing it as PID 0xFE38.
        // Bin channel sits at 0x2000 returning row+1 per row.
        var elapsed = new long[] { 0, 10, 20, 30 };
        var headers = new[]
        {
            new BinChannelHeader(1, "X", "u", 0x2000, 2, 0x7E8, 2, 1.0, 0),
        };
        var src = new FakeBinSource(elapsed, headers, (ch, row) => row + 1);

        var coord = new BinReplayCoordinator();
        coord.Load(src);

        var build = BinChannelToPid.BuildEcus(headers, coord);
        var ecm = build.Nodes.Single();

        var ch = NewChannel();

        // Host issues $2D 0xFE38 0x2000 0x02 (alias 0x2000 as PID 0xFE38, size 2).
        // Wire bytes: SID(1) + PID(2) + addr(2) + MS(1) = 6 payload bytes.
        var define = new byte[] { 0x2D, 0xFE, 0x38, 0x20, 0x00, 0x02 };
        bool ok = Service2DHandler.Handle(ecm, define, ch);
        Assert.True(ok);
        ch.RxQueue.TryDequeue(out _);          // discard $6D positive response

        // Latch playback start so subsequent samples reference a known offset.
        coord.MaybeStart(0);

        // Host issues $22 0xFE38 at busNow=10 -> replayMs=10 -> row 1 -> value=2.
        var read = new byte[] { 0x22, 0xFE, 0x38 };
        Service22Handler.Handle(ecm, read, ch, timeMs: 10);

        Assert.True(ch.RxQueue.TryDequeue(out var resp));
        // Layout: CAN(4) + PCI(1) + SID(1) + PID(2) + value(2) = 10 bytes
        Assert.Equal(0x62, resp!.Data[5]);
        Assert.Equal(new byte[] { 0xFE, 0x38 }, resp.Data[6..8]);
        // Bin row 1 returned 2, scalar=1, offset=0 -> raw=2 -> 0x0002.
        Assert.Equal(new byte[] { 0x00, 0x02 }, resp.Data[8..10]);
    }

    [Fact]
    public void Define_AliasOfNonBinAddress_StillWorksAsBefore()
    {
        // No bin loaded; existing static PID at 0x1234 with a synthetic
        // ConstantWaveform. $2D alias should clone it without crashing.
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();

        var define = new byte[] { 0x2D, 0xFE, 0x38, 0x12, 0x34, 0x02 };
        bool ok = Service2DHandler.Handle(node, define, ch);
        Assert.True(ok);
        ch.RxQueue.TryDequeue(out _);

        // Read it - should match the static PID's encoded value.
        Service22Handler.Handle(node, new byte[] { 0x22, 0xFE, 0x38 }, ch, timeMs: 0);
        Assert.True(ch.RxQueue.TryDequeue(out var resp));
        Assert.Equal(0x62, resp!.Data[5]);
        // The TestEcus PID at 0x1234 is a Constant 80°C with scalar=0.0625, offset=-40
        // -> raw = (80 - -40)/0.0625 = 1920 = 0x0780.
        Assert.Equal(new byte[] { 0x07, 0x80 }, resp.Data[8..10]);
    }
}
