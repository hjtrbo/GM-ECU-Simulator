using Common.Persistence;
using Common.Protocol;
using Common.Waveforms;

namespace EcuSimulator.Tests.Common;

public class ConfigSerializerTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = new SimulatorConfig
        {
            Version = SimulatorConfig.CurrentVersion,
            Description = "test",
            Ecus =
            {
                new EcuDto
                {
                    Name = "ECM",
                    PhysicalRequestCanId = 0x241,
                    UsdtResponseCanId = 0x641,
                    UudtResponseCanId = 0x541,
                    Pids =
                    {
                        new PidDto
                        {
                            Address = 0x1234,
                            Name = "Coolant",
                            Size = PidSize.Word,
                            DataType = PidDataType.Unsigned,
                            Scalar = 0.0625,
                            Offset = -40.0,
                            Unit = "C",
                            Waveform = new WaveformDto
                            {
                                Shape = WaveformShape.Sin,
                                Amplitude = 50, Offset = 80, FrequencyHz = 0.2,
                            },
                        },
                    },
                },
            },
        };

        var json = ConfigSerializer.Serialize(original);
        var roundTripped = ConfigSerializer.Deserialize(json);

        Assert.Single(roundTripped.Ecus);
        var ecu = roundTripped.Ecus[0];
        Assert.Equal("ECM", ecu.Name);
        Assert.Equal((ushort)0x241, ecu.PhysicalRequestCanId);
        Assert.Equal((ushort)0x641, ecu.UsdtResponseCanId);
        Assert.Equal((ushort)0x541, ecu.UudtResponseCanId);
        Assert.Single(ecu.Pids);
        var pid = ecu.Pids[0];
        Assert.Equal((ushort)0x1234, pid.Address);
        Assert.Equal(PidSize.Word, pid.Size);
        Assert.Equal(0.0625, pid.Scalar);
        Assert.Equal(-40.0, pid.Offset);
        Assert.Equal(WaveformShape.Sin, pid.Waveform.Shape);
        Assert.Equal(50.0, pid.Waveform.Amplitude);
    }

    [Fact]
    public void HexCanIdsSerializeAsHexStrings()
    {
        var cfg = new SimulatorConfig
        {
            Ecus = { new EcuDto { Name = "X", PhysicalRequestCanId = 0x241,
                                   UsdtResponseCanId = 0x641, UudtResponseCanId = 0x541 } },
        };
        var json = ConfigSerializer.Serialize(cfg);
        Assert.Contains("\"0x241\"", json);
        Assert.Contains("\"0x641\"", json);
    }

    [Fact]
    public void DeserializeAcceptsBothHexStringsAndDecimals()
    {
        var json = """
            {
              "version": 1,
              "ecus": [
                {
                  "name": "ECM",
                  "physicalRequestCanId": "0x241",
                  "usdtResponseCanId": 1601,
                  "uudtResponseCanId": "0x541",
                  "pids": []
                }
              ]
            }
            """;
        var cfg = ConfigSerializer.Deserialize(json);
        Assert.Equal((ushort)0x241, cfg.Ecus[0].PhysicalRequestCanId);
        Assert.Equal((ushort)1601, cfg.Ecus[0].UsdtResponseCanId);  // 1601 == 0x641
    }

    [Fact]
    public void VersionMismatch_Throws()
    {
        var json = "{ \"version\": 999, \"ecus\": [] }";
        Assert.Throws<InvalidDataException>(() => ConfigSerializer.Deserialize(json));
    }

    [Fact]
    public void EnumWaveformShapeRoundTripsAsCamelCaseString()
    {
        var cfg = new SimulatorConfig
        {
            Ecus = { new EcuDto { Name = "X", PhysicalRequestCanId = 1, UsdtResponseCanId = 2, UudtResponseCanId = 3,
                                   Pids = { new PidDto { Address = 0, Name = "p", Size = PidSize.Byte, DataType = PidDataType.Unsigned,
                                                          Waveform = new WaveformDto { Shape = WaveformShape.Triangle } } } } },
        };
        var json = ConfigSerializer.Serialize(cfg);
        Assert.Contains("\"triangle\"", json, StringComparison.OrdinalIgnoreCase);
        var rt = ConfigSerializer.Deserialize(json);
        Assert.Equal(WaveformShape.Triangle, rt.Ecus[0].Pids[0].Waveform.Shape);
    }
}
