using Core.Persistence;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Persistence;

// Guards the per-ECU RamReadReturnsZeros flag through ConfigStore's node<->dto
// round-trip so the "answer RAM reads with zeros" setting survives a save/load.
public sealed class RamReadZerosRoundTripTests
{
    [Fact]
    public void RamReadReturnsZeros_RoundTripsThroughConfigStore()
    {
        var node = NodeFactory.CreateNode();
        node.RamReadReturnsZeros = true;

        var dto = ConfigStore.EcuDtoFrom(node);
        Assert.True(dto.RamReadReturnsZeros);

        var back = ConfigStore.EcuNodeFrom(dto);
        Assert.True(back.RamReadReturnsZeros);
    }

    [Fact]
    public void RamReadReturnsZeros_DefaultsToFalse()
    {
        var node = NodeFactory.CreateNode();
        Assert.False(node.RamReadReturnsZeros);

        // A dto with the field unset (older config) loads as false.
        var back = ConfigStore.EcuNodeFrom(ConfigStore.EcuDtoFrom(node));
        Assert.False(back.RamReadReturnsZeros);
    }
}
