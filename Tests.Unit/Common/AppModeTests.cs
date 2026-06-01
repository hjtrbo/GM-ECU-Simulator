using Common;
using Xunit;

namespace EcuSimulator.Tests.AppModes;

public sealed class AppModeTests
{
    [Theory]
    [InlineData(AppMode.EcuSimulator, true)]
    [InlineData(AppMode.DpsSimulator, false)]
    public void AllowsMultipleEcus_IsTrueOnlyForEcuSimulator(AppMode mode, bool expected)
        => Assert.Equal(expected, mode.AllowsMultipleEcus());

    [Theory]
    [InlineData(AppMode.EcuSimulator, true)]
    [InlineData(AppMode.DpsSimulator, false)]
    public void PersistsConfig_IsFalseForDpsModes(AppMode mode, bool expected)
        => Assert.Equal(expected, mode.PersistsConfig());

    [Theory]
    [InlineData(AppMode.EcuSimulator, "ecu_simulator.mode.json")]
    [InlineData(AppMode.DpsSimulator, "dps_simulator.mode.json")]
    public void ConfigFileName_HasStableNamePerMode(AppMode mode, string expected)
        => Assert.Equal(expected, mode.ConfigFileName());

    [Theory]
    [InlineData(AppMode.EcuSimulator, "ECU Simulator")]
    [InlineData(AppMode.DpsSimulator, "DPS Simulator")]
    public void DisplayName_MatchesUserVocabulary(AppMode mode, string expected)
        => Assert.Equal(expected, mode.DisplayName());

    [Fact]
    public void DisplayNames_AreAllDistinct()
    {
        var names = new[]
        {
            AppMode.EcuSimulator.DisplayName(),
            AppMode.DpsSimulator.DisplayName(),
        };
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public void ConfigFileNames_AreAllDistinct()
    {
        var names = new[]
        {
            AppMode.EcuSimulator.ConfigFileName(),
            AppMode.DpsSimulator.ConfigFileName(),
        };
        Assert.Equal(names.Length, names.Distinct().Count());
    }
}
