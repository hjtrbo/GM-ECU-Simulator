using Common.Dbc;
using Common.Persistence;
using Common.Protocol;
using Common.Signals;
using Core.Ecu;
using Core.Persistence;
using System.Text.Json;
using Xunit;

namespace EcuSimulator.Tests.Persistence;

// Round-trip for the v18 EcuDto.Broadcasts and the standalone *.dbc.json shape
// (a flat List<BroadcastMessageDto>).
public sealed class BroadcastPersistenceTests
{
    private static EcuNode NodeWithBroadcast()
    {
        var node = new EcuNode { Name = "ECM", PhysicalRequestCanId = 0x7E0, UsdtResponseCanId = 0x7E8, UudtResponseCanId = 0x5E8 };
        var msg = new BroadcastMessage { CanId = 0x0C9, Name = "Engine_General", Dlc = 8, PeriodMs = 48, Enabled = true };
        msg.Signals.Add(new BroadcastSignal
        {
            Name = "ENGINE_SPEED", StartBit = 7, Length = 16, ByteOrder = DbcByteOrder.Motorola,
            Scale = 0.25, Unit = "rpm", ValueSource = BroadcastValueSource.Signal, Signal = SignalId.EngineRpm,
        });
        node.AddBroadcast(msg);
        return node;
    }

    [Fact]
    public void EcuDto_RoundTrip_PreservesBroadcasts()
    {
        var dto = ConfigStore.EcuDtoFrom(NodeWithBroadcast());
        var node2 = ConfigStore.EcuNodeFrom(dto);

        var msg = Assert.Single(node2.Broadcasts);
        Assert.Equal(0x0C9u, msg.CanId);
        Assert.Equal(48, msg.PeriodMs);
        var sig = Assert.Single(msg.Signals);
        Assert.Equal("ENGINE_SPEED", sig.Name);
        Assert.Equal(0.25, sig.Scale, 6);
        Assert.Equal(BroadcastValueSource.Signal, sig.ValueSource);
        Assert.Equal(SignalId.EngineRpm, sig.Signal);
    }

    [Fact]
    public void ConfigWithoutBroadcasts_LoadsWithEmptyList()
    {
        // A v17-style ECU (no Broadcasts member) must load with node.Broadcasts empty, not throw.
        var dto = new EcuDto
        {
            Name = "ECM", PhysicalRequestCanId = 0x7E0, UsdtResponseCanId = 0x7E8, UudtResponseCanId = 0x5E8,
            Broadcasts = null,
        };
        var node = ConfigStore.EcuNodeFrom(dto);
        Assert.Empty(node.Broadcasts);
    }

    [Fact]
    public void DbcJson_FlatList_RoundTripsWithCamelCaseEnums()
    {
        // The *.dbc.json save shape: a flat List<BroadcastMessageDto> through the shared options.
        var list = ConfigStore.EcuDtoFrom(NodeWithBroadcast()).Broadcasts!;
        string json = JsonSerializer.Serialize(list, ConfigSerializer.Options);
        var back = JsonSerializer.Deserialize<List<BroadcastMessageDto>>(json, ConfigSerializer.Options)!;

        var msg = Assert.Single(back);
        Assert.Equal(0x0C9u, msg.CanId);
        var sig = Assert.Single(msg.Signals);
        Assert.Equal(SignalId.EngineRpm, sig.Signal);
        Assert.Equal(BroadcastValueSource.Signal, sig.ValueSource);
        // camelCase enum on the wire (e.g. "signal", "motorola").
        Assert.Contains("\"signal\"", json);
        Assert.Contains("\"motorola\"", json);
    }
}
