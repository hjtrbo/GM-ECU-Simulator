using Common.Dbc;
using Common.Protocol;
using Common.Signals;
using Core.Dbc;
using Core.Ecu;
using Xunit;

namespace EcuSimulator.Tests.Dbc;

// DbcImporter: auto-mapping heuristic, scoped (transmitter + id) import, and merge/replace semantics.
public sealed class DbcImporterTests
{
    private static string LocateResource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "resources", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate resources/" + fileName);
    }

    private static DbcSignal Sig(string name, string unit = "", int len = 16)
        => new() { Name = name, StartBit = 7, Length = len, ByteOrder = DbcByteOrder.Motorola, Scale = 1, Unit = unit };

    [Fact]
    public void AutoMap_MapsEngineSpeedAndVehicleSpeed_LeavesRateOfChangeUnmapped()
    {
        Assert.Equal(SignalId.EngineRpm, DbcImporter.AutoMap(Sig("Engine_Speed", "rpm")));
        Assert.Equal(SignalId.VehicleSpeed, DbcImporter.AutoMap(Sig("Vehicle_Speed", "km/h")));
        Assert.Null(DbcImporter.AutoMap(Sig("Engine_Speed_ROC", "rpm/s")));
        Assert.Equal(SignalId.CoolantTemp, DbcImporter.AutoMap(Sig("Engine_Coolant_Temp", "degC", 8)));
    }

    [Fact]
    public void ToBroadcasts_ScopesToTransmitterAndSelectedIds_AutoMapsRpm()
    {
        var db = DbcParser.Parse(File.ReadAllText(LocateResource("FG_Falcon_HighSpeed_CAN.dbc")));
        var msgs = DbcImporter.ToBroadcasts(db, "Vector__XXX", new HashSet<uint> { 519 });

        var msg = Assert.Single(msgs);
        Assert.Equal(519u, msg.CanId);
        Assert.Equal(DbcImporter.DefaultPeriodMs, msg.PeriodMs);   // FG file has no GenMsgCycleTime

        var rpm = msg.Signals.Single(s => s.Name == "Engine_Speed");
        Assert.Equal(BroadcastValueSource.Signal, rpm.ValueSource);
        Assert.Equal(SignalId.EngineRpm, rpm.Signal);
    }

    [Fact]
    public void TransmittersByMessageCount_OrdersByCountDescending()
    {
        var db = DbcParser.Parse(File.ReadAllText(LocateResource("GlobalA - HS.dbc")));
        var tx = DbcImporter.TransmittersByMessageCount(db);
        Assert.Equal("ECM_HS", tx[0].Transmitter);                 // most prolific on a GM HS bus
        Assert.True(tx[0].Count >= tx[1].Count);
    }

    [Fact]
    public void ReplaceBroadcasts_ClearsPriorSet()
    {
        var node = NodeNew();
        node.AddBroadcast(new BroadcastMessage { CanId = 0x111, Name = "old" });
        node.ReplaceBroadcasts(new[] { new BroadcastMessage { CanId = 0x222, Name = "new" } });

        var only = Assert.Single(node.Broadcasts);
        Assert.Equal(0x222u, only.CanId);
    }

    private static EcuNode NodeNew() => new() { Name = "ECM", PhysicalRequestCanId = 0x7E0, UsdtResponseCanId = 0x7E8, UudtResponseCanId = 0x5E8 };
}
