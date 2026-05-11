using Common.Persistence;
using Common.Replay;

namespace EcuSimulator.Tests.Replay;

// Round-trip tests for the new BinReplay section in SimulatorConfig and the
// v1 -> v2 backwards compatibility (v1 files have no BinReplay field).
public class ReplayConfigSerializerTests
{
    [Fact]
    public void RoundTrip_PreservesBinReplaySection()
    {
        var cfg = new SimulatorConfig
        {
            Version = SimulatorConfig.CurrentVersion,
            BinReplay = new BinReplayConfig
            {
                FilePath = @"C:\logs\session_2025_05_10.bin",
                AutoLoadOnStart = true,
                LoopMode = BinReplayLoopMode.Loop,
            },
        };
        var json = ConfigSerializer.Serialize(cfg);
        var roundTripped = ConfigSerializer.Deserialize(json);

        Assert.NotNull(roundTripped.BinReplay);
        Assert.Equal(@"C:\logs\session_2025_05_10.bin", roundTripped.BinReplay!.FilePath);
        Assert.True(roundTripped.BinReplay.AutoLoadOnStart);
        Assert.Equal(BinReplayLoopMode.Loop, roundTripped.BinReplay.LoopMode);
    }

    [Fact]
    public void V1Config_DeserialisesWithBinReplayNull()
    {
        // Hand-crafted v1 JSON (no BinReplay field) - shape that existed
        // before this feature landed. Loader must accept it.
        const string v1Json = """
        {
          "version": 1,
          "ecus": []
        }
        """;
        var cfg = ConfigSerializer.Deserialize(v1Json);
        Assert.Equal(1, cfg.Version);
        Assert.Null(cfg.BinReplay);
    }

    [Fact]
    public void UnsupportedFutureVersion_IsRejected()
    {
        const string futureJson = """
        {
          "version": 99,
          "ecus": []
        }
        """;
        Assert.Throws<InvalidDataException>(() => ConfigSerializer.Deserialize(futureJson));
    }

    [Fact]
    public void NullBinReplay_OmittedFromOutput()
    {
        // Default-ignore-null-when-writing keeps the output clean for users
        // who never load a bin.
        var cfg = new SimulatorConfig { Version = SimulatorConfig.CurrentVersion };
        var json = ConfigSerializer.Serialize(cfg);
        Assert.DoesNotContain("binReplay", json, StringComparison.Ordinal);
    }
}
