using Common.PassThru;
using Core.Bus;
using Core.Ecu.Personas;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Bus;

// Phase 6 broadcast plumbing. Covers:
//   - VirtualBus.Broadcaster is null by default (no IPC session bound).
//   - FordUdsPersona doesn't crash when bus.Broadcaster is null and
//     EnsureBroadcastStarted fires (timer ticks become no-ops).
//   - When a IFrameBroadcaster fake is wired, the broadcast tick reaches it.
[Collection(FordUdsPersonaCollection.Name)]
public sealed class FrameBroadcasterTests
{
    private sealed class FakeBroadcaster : IFrameBroadcaster
    {
        public List<byte[]> Frames { get; } = new();
        public void BroadcastFrame(byte[] frame) { lock (Frames) Frames.Add(frame); }
    }

    [Fact]
    public void VirtualBus_Default_BroadcasterIsNull()
    {
        var bus = new VirtualBus();
        Assert.Null(bus.Broadcaster);
    }

    [Fact]
    public void FordUdsPersona_BroadcastReachesBroadcaster()
    {
        // Wire a fake broadcaster, send an $A1 to register a slot, send $A0
        // to kick off the broadcast loop, sleep a beat, expect a frame.
        // Drives the real TimerOnDelay so the test is integration-flavoured.
        FordUdsPersona.StopBroadcast();           // clean slate
        FordUdsPersona.ResetDmrSlotMap();
        var bus = new VirtualBus();
        var fake = new FakeBroadcaster();
        bus.Broadcaster = fake;

        var node = NodeFactory.CreateNode();
        node.Persona = FordUdsPersona.Instance;
        var ch = new ChannelSession
        {
            Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus,
        };

        // Bind slot 0x08 -> 0x003F86EC via $A1 (verbatim PCMTec capture).
        byte[] a1 = { 0xA1, 0x08, 0x8C, 0x00, 0x3F, 0x86, 0xEC };
        node.Persona.Dispatch(node, a1, ch, false, 0xA1, 0,
            new Core.Scheduler.DpidScheduler(bus), Common.Protocol.DiagnosticStack.Uds);
        ch.RxQueue.TryDequeue(out _); // drain echo

        // $A0 starts the broadcast.
        byte[] a0 = { 0xA0, 0x08 };
        node.Persona.Dispatch(node, a0, ch, false, 0xA0, 0,
            new Core.Scheduler.DpidScheduler(bus), Common.Protocol.DiagnosticStack.Uds);

        // Give the 100ms timer a couple of cycles to fire.
        Thread.Sleep(350);

        FordUdsPersona.StopBroadcast();

        Assert.NotEmpty(fake.Frames);
        // Each tick emits TWO frames now: one DMR-stream broadcast (0x6A0
        // or 0x6A4 alternating, slot byte at position 4) AND one engine-bus
        // RPM broadcast (0x97, with RPM-encoded bytes 0x0C 0x80 at positions
        // 4-5). Split by CAN ID and assert both kinds appear.
        var canIdsSeen = new HashSet<(byte, byte)>();
        bool sawDmrSlot = false;
        bool sawRpm = false;
        foreach (var f in fake.Frames)
        {
            Assert.Equal(12, f.Length);
            canIdsSeen.Add((f[2], f[3]));
            if (f[2] == 0x06 && (f[3] == 0xA0 || f[3] == 0xA4))
            {
                Assert.Equal(0x08, f[4]); // only slot 0x08 bound
                sawDmrSlot = true;
            }
            else if (f[2] == 0x00 && f[3] == 0x97)
            {
                Assert.Equal(0x0C, f[4]); // RPM high byte (800 rpm * 4 = 0x0C80)
                Assert.Equal(0x80, f[5]); // RPM low byte
                sawRpm = true;
            }
        }
        Assert.True(sawDmrSlot, "expected DMR-stream broadcast on 0x6A0 or 0x6A4");
        Assert.True(sawRpm,     "expected engine-bus RPM broadcast on 0x97");
    }

    [Fact]
    public void StopBroadcast_IsSafeWhenNothingRunning()
    {
        FordUdsPersona.StopBroadcast(); // no exception
        FordUdsPersona.StopBroadcast();
    }
}
