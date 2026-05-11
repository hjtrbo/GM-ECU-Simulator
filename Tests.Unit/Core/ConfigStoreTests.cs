using Common.Persistence;
using Common.Protocol;
using Common.Waveforms;
using Core.Bus;
using Core.Ecu;
using Core.Persistence;

namespace EcuSimulator.Tests.Core;

public class ConfigStoreTests
{
    [Fact]
    public void Snapshot_CapturesCurrentBusState()
    {
        var bus = new VirtualBus();
        bus.AddNode(new EcuNode
        {
            Name = "ECM",
            PhysicalRequestCanId = 0x241,
            UsdtResponseCanId = 0x641,
            UudtResponseCanId = 0x541,
        });
        var pid = new Pid
        {
            Address = 0x1234,
            Name = "Coolant",
            Size = PidSize.Word,
            DataType = PidDataType.Unsigned,
            Scalar = 0.0625,
            Offset = -40,
            Unit = "C",
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Sin, Amplitude = 50, Offset = 80, FrequencyHz = 0.2 },
        };
        bus.Nodes[0].AddPid(pid);

        var snapshot = ConfigStore.Snapshot(bus);
        Assert.Single(snapshot.Ecus);
        Assert.Equal("ECM", snapshot.Ecus[0].Name);
        Assert.Single(snapshot.Ecus[0].Pids);
        Assert.Equal((ushort)0x1234, snapshot.Ecus[0].Pids[0].Address);
        Assert.Equal(WaveformShape.Sin, snapshot.Ecus[0].Pids[0].Waveform.Shape);
    }

    [Fact]
    public void ApplyTo_ReplacesBusContents()
    {
        var bus = new VirtualBus();
        bus.AddNode(new EcuNode
        {
            Name = "OldEcu", PhysicalRequestCanId = 0x111,
            UsdtResponseCanId = 0x222, UudtResponseCanId = 0x333,
        });
        Assert.Single(bus.Nodes);

        var newCfg = new SimulatorConfig
        {
            Version = SimulatorConfig.CurrentVersion,
            Ecus =
            {
                new EcuDto
                {
                    Name = "NewEcm",
                    PhysicalRequestCanId = 0x241, UsdtResponseCanId = 0x641, UudtResponseCanId = 0x541,
                    Pids = { new PidDto
                    {
                        Address = 0xABCD, Name = "p", Size = PidSize.Byte, DataType = PidDataType.Unsigned,
                        Waveform = new WaveformDto { Shape = WaveformShape.Constant, Offset = 42 },
                    } },
                },
            },
        };
        ConfigStore.ApplyTo(newCfg, bus);

        Assert.Single(bus.Nodes);
        Assert.Equal("NewEcm", bus.Nodes[0].Name);
        Assert.Single(bus.Nodes[0].Pids);
        Assert.NotNull(bus.Nodes[0].GetPid(0xABCD));
    }

    [Fact]
    public void SaveAndLoad_FullRoundTripPreservesValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ecu_cfg_{Guid.NewGuid():N}.json");
        try
        {
            var bus1 = new VirtualBus();
            ConfigStore.ApplyTo(DefaultEcuConfig.Build(), bus1);

            ConfigStore.Save(ConfigStore.Snapshot(bus1), path);

            var bus2 = new VirtualBus();
            ConfigStore.ApplyTo(ConfigStore.Load(path), bus2);

            Assert.Equal(bus1.Nodes.Count, bus2.Nodes.Count);
            for (int i = 0; i < bus1.Nodes.Count; i++)
            {
                var a = bus1.Nodes[i]; var b = bus2.Nodes[i];
                Assert.Equal(a.Name, b.Name);
                Assert.Equal(a.PhysicalRequestCanId, b.PhysicalRequestCanId);
                Assert.Equal(a.Pids.Count, b.Pids.Count);
                for (int j = 0; j < a.Pids.Count; j++)
                {
                    Assert.Equal(a.Pids[j].Address, b.Pids[j].Address);
                    // Waveforms produce equal samples — proof that scaling + shape survived.
                    Assert.Equal(a.Pids[j].Waveform.Sample(0), b.Pids[j].Waveform.Sample(0), 6);
                }
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ApplyTo_ClearsActivePeriodicSchedule()
    {
        var bus = new VirtualBus();
        ConfigStore.ApplyTo(DefaultEcuConfig.Build(), bus);
        var ecm = bus.Nodes[0];
        var ch = new ChannelSession { Id = 1, Protocol = global::Common.PassThru.ProtocolID.CAN, Baud = 500000 };
        var dpid = new Dpid { Id = 0xFE, Pids = new[] { ecm.GetPid(0x1234)! } };
        ecm.AddDpid(dpid);
        bus.Scheduler.Add(ecm, dpid, ch, DpidRate.Fast);

        // Replace the bus with a fresh config — periodic should stop.
        ConfigStore.ApplyTo(new SimulatorConfig { Version = SimulatorConfig.CurrentVersion }, bus);

        Thread.Sleep(150);
        while (ch.RxQueue.TryDequeue(out _)) { }
        Thread.Sleep(150);
        Assert.Empty(ch.RxQueue);
    }
}
