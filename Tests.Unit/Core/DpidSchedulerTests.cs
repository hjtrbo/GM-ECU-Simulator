using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace EcuSimulator.Tests.Core;

public class DpidSchedulerTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    [Fact]
    public void FastBand_EmitsApproximately25HzFrames()
    {
        var bus = new VirtualBus();
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        var ch = NewChannel();
        var dpid = new Dpid { Id = 0xFE, Pids = new[] { node.GetPid(0x1234)!, node.GetPid(0x5678)! } };
        node.AddDpid(dpid);

        bus.Scheduler.Add(node, dpid, ch, DpidRate.Fast);
        Thread.Sleep(500);                                  // ~12 frames at 25 Hz
        bus.Scheduler.Stop(node, Array.Empty<byte>());

        // Allow generous slack — TimerOnDelay aims for ±1 ms but we allow 6..18 here
        // to keep the test stable on a loaded CI agent.
        var n = ch.RxQueue.Count;
        Assert.InRange(n, 6, 18);
    }

    [Fact]
    public void StopAll_HaltsAllBands()
    {
        var bus = new VirtualBus();
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        var ch = NewChannel();
        var dpid = new Dpid { Id = 0xFE, Pids = new[] { node.GetPid(0x1234)! } };
        node.AddDpid(dpid);

        bus.Scheduler.Add(node, dpid, ch, DpidRate.Fast);
        Thread.Sleep(150);
        bus.Scheduler.Stop(node, Array.Empty<byte>());
        // Drain everything currently buffered.
        while (ch.RxQueue.TryDequeue(out _)) { }
        Thread.Sleep(200);
        Assert.Empty(ch.RxQueue);
    }

    [Fact]
    public void ReissueChangesRate()
    {
        var bus = new VirtualBus();
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        var ch = NewChannel();
        var dpid = new Dpid { Id = 0xFE, Pids = new[] { node.GetPid(0x1234)! } };
        node.AddDpid(dpid);

        bus.Scheduler.Add(node, dpid, ch, DpidRate.Slow);
        bus.Scheduler.Add(node, dpid, ch, DpidRate.Fast);   // override Slow with Fast
        Thread.Sleep(200);
        bus.Scheduler.Stop(node, Array.Empty<byte>());
        // At Fast (40ms) we expect ~5 frames in 200ms; Slow (1000ms) would have produced 0.
        Assert.True(ch.RxQueue.Count >= 3, $"expected >= 3 Fast frames, got {ch.RxQueue.Count}");
    }

    [Fact]
    public void SendOnce_EmitsExactlyOneFrame()
    {
        var bus = new VirtualBus();
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        var ch = NewChannel();
        var dpid = new Dpid { Id = 0xFE, Pids = new[] { node.GetPid(0x1234)! } };

        bus.Scheduler.SendOnce(node, dpid, ch);

        Assert.Single(ch.RxQueue);
    }

    [Fact]
    public void BuildUudtFrame_LayoutMatchesGmlanConvention()
    {
        var node = TestEcus.BuildEcm();
        var dpid = new Dpid { Id = 0xFE, Pids = new[] { node.GetPid(0x1234)! } };
        var frame = global::Core.Scheduler.DpidScheduler.BuildUudtFrame(node, dpid, timeMs: 0);

        // CAN(4) + DPID(1) + value(2) = 7 bytes
        Assert.Equal(7, frame.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x05, 0x41 }, frame[0..4]);   // UUDT response 0x541
        Assert.Equal(0xFE, frame[4]);                                         // DPID id
        Assert.Equal(new byte[] { 0x07, 0x80 }, frame[5..7]);                 // 80°C encoded
    }
}
