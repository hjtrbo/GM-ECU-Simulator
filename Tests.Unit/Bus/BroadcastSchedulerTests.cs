using Common.Dbc;
using Core.Bus;
using Core.Ecu;
using Core.Scheduler;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Bus;

// BroadcastScheduler emit + lifecycle. Uses a fake broadcaster and a constant-valued signal so the
// frame bytes are deterministic (no dependence on the live engine model), then drives the real
// TimerOnDelay for a couple of cycles like the Ford-broadcast test does.
public sealed class BroadcastSchedulerTests
{
    private sealed class FakeBroadcaster : IFrameBroadcaster
    {
        public List<byte[]> Frames { get; } = new();
        public void BroadcastFrame(byte[] frame) { lock (Frames) Frames.Add((byte[])frame.Clone()); }
        public int Count { get { lock (Frames) return Frames.Count; } }
        public void Clear() { lock (Frames) Frames.Clear(); }
        public byte[][] Snapshot() { lock (Frames) return Frames.ToArray(); }
    }

    private static EcuNode NodeWithConstantRpmBroadcast(uint canId, int periodMs)
    {
        var node = NodeFactory.CreateNode();
        var msg = new BroadcastMessage { CanId = canId, Dlc = 8, PeriodMs = periodMs, Enabled = true };
        msg.Signals.Add(new BroadcastSignal
        {
            Name = "ENGINE_SPEED",
            StartBit = 7, Length = 16, ByteOrder = DbcByteOrder.Motorola,
            Scale = 1.0,
            ValueSource = Common.Protocol.BroadcastValueSource.Constant,
            Constant = 3200,   // raw 3200 = 0x0C80 -> bytes 0x0C, 0x80
        });
        node.AddBroadcast(msg);
        return node;
    }

    [Fact]
    public void EnabledMessage_EmitsFrameWithCanIdAndPackedBytes()
    {
        var bus = new VirtualBus();
        var fake = new FakeBroadcaster();
        bus.Broadcaster = fake;
        bus.AddNode(NodeWithConstantRpmBroadcast(0x0C9, 40));

        bus.BroadcastScheduler.RebuildAndStart();
        Thread.Sleep(150);
        bus.BroadcastScheduler.StopAll();

        var frames = fake.Snapshot();
        Assert.NotEmpty(frames);
        var f = frames[0];
        Assert.Equal(12, f.Length);                 // 4-byte CAN id + 8-byte payload
        Assert.Equal(0x00, f[0]);
        Assert.Equal(0xC9, f[3]);                   // CAN id 0x0C9 big-endian in bytes 0..3
        Assert.Equal(0x0C, f[4]);                   // RPM high
        Assert.Equal(0x80, f[5]);                   // RPM low
    }

    [Fact]
    public void StopAll_HaltsEmission()
    {
        var bus = new VirtualBus();
        var fake = new FakeBroadcaster();
        bus.Broadcaster = fake;
        bus.AddNode(NodeWithConstantRpmBroadcast(0x0C9, 30));

        bus.BroadcastScheduler.RebuildAndStart();
        Thread.Sleep(100);
        bus.BroadcastScheduler.StopAll();
        fake.Clear();
        Thread.Sleep(120);

        Assert.Equal(0, fake.Count);
    }

    [Fact]
    public void DisabledMessage_IsNotScheduled()
    {
        var bus = new VirtualBus();
        var fake = new FakeBroadcaster();
        bus.Broadcaster = fake;
        var node = NodeWithConstantRpmBroadcast(0x0C9, 30);
        node.Broadcasts[0].Enabled = false;          // snapshot is a copy, but the message object is shared
        bus.AddNode(node);

        bus.BroadcastScheduler.RebuildAndStart();
        Thread.Sleep(100);
        bus.BroadcastScheduler.StopAll();

        Assert.Equal(0, fake.Count);
    }
}
