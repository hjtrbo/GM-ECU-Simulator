using Common.PassThru;
using Common.Protocol;
using Common.Replay;
using Core.Bus;
using Core.Ecu;
using Core.Replay;
using Core.Services;

namespace EcuSimulator.Tests.Replay;

// Full-stack: bin loaded, ECU built from the bin, $22 dispatched - the
// response encodes the bin sample at (busNowMs - replayStartMs).
public class Service22ReplayTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    [Fact]
    public void Service22_ReturnsReplaySample_AtCurrentBusTime()
    {
        // 100 rows at 10 ms intervals. Channel 0 row r returns r + 1
        // (so row 5 -> 6, row 10 -> 11, etc). PID address 0x000C.
        var elapsed = new long[100];
        for (int i = 0; i < 100; i++) elapsed[i] = i * 10;
        var headers = new[]
        {
            new BinChannelHeader(1, "RPM", "RPM", 0x000C, 2, 0x7E8, 2, 1.0, 0),
        };
        var src = new FakeBinSource(elapsed, headers, (ch, row) => row + 1);

        var coord = new BinReplayCoordinator();
        coord.Load(src);

        var build = BinChannelToPid.BuildEcus(headers, coord);
        var ecm = build.Nodes.Single();

        // Simulate "first $22 arrives" -> coordinator latches start=100ms.
        coord.MaybeStart(100);

        // Now call Service22Handler with timeMs=150 -> replayMs=50 -> row 5 -> value=6.
        var ch = NewChannel();
        Service22Handler.Handle(ecm, new byte[] { 0x22, 0x00, 0x0C }, ch, timeMs: 150);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        // Response layout: CAN(4) + PCI(1) + SID(1) + PID(2) + value(2) = 10 bytes
        // for a Word-sized PID with Scalar=1.0/Offset=0 (raw = round(eng) = 6 = 0x0006).
        Assert.Equal(10, msg!.Data.Length);
        Assert.Equal(0x62, msg.Data[5]);                          // positive $22
        Assert.Equal(new byte[] { 0x00, 0x0C }, msg.Data[6..8]);  // PID echo
        Assert.Equal(new byte[] { 0x00, 0x06 }, msg.Data[8..10]); // bin value at row 5
    }

    [Fact]
    public void DispatchUsdt_FirstService22_LatchesStartTime()
    {
        // Wires the coordinator into VirtualBus and verifies the start hook
        // inside DispatchUsdt fires. We simulate the dispatch by calling
        // through the public DispatchHostTx with a single-frame $22.
        var bus = new VirtualBus();
        var coord = new BinReplayCoordinator(bus);
        bus.Replay = coord;

        var elapsed = new long[] { 0, 100 };
        var headers = new[]
        {
            new BinChannelHeader(1, "RPM", "RPM", 0x000C, 2, 0x7E8, 2, 1.0, 0),
        };
        coord.Load(new FakeBinSource(elapsed, headers, (ch, row) => 1));

        var build = BinChannelToPid.BuildEcus(headers, coord);
        bus.ReplaceNodes(build.Nodes);

        Assert.Equal(BinReplayState.Armed, coord.State);

        var ch = NewChannel();
        // Build a single-frame CAN $22 request. CanFrame layout: 4-byte ID + payload.
        // Request goes to 0x7E0 (the request ID for the auto-built ECM).
        var frame = new byte[4 + 8];
        frame[0] = 0; frame[1] = 0; frame[2] = 0x07; frame[3] = 0xE0; // ID = 0x7E0
        frame[4] = 0x03;                                              // PCI: SF, len=3
        frame[5] = 0x22; frame[6] = 0x00; frame[7] = 0x0C;            // $22 0x000C
        bus.DispatchHostTx(frame, ch);

        Assert.Equal(BinReplayState.Running, coord.State);
        Assert.True(ch.RxQueue.TryDequeue(out _));        // response was generated
    }
}
