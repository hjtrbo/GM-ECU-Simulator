using Common.Protocol;
using Common.Waveforms;
using Core.Ecu;

namespace EcuSimulator.Tests.Core;

// Test fixtures for service handler tests — a stub ECM with two PIDs.
internal static class TestEcus
{
    public static EcuNode BuildEcm()
    {
        var node = new EcuNode
        {
            Name = "ECM",
            PhysicalRequestCanId = 0x241,
            UsdtResponseCanId = 0x641,
            UudtResponseCanId = 0x541,
        };
        node.AddPid(new Pid
        {
            Address = 0x1234,
            Name = "TempSensor",
            Size = PidSize.Word,
            DataType = PidDataType.Unsigned,
            Scalar = 0.0625,
            Offset = -40.0,
            Unit = "°C",
            // Eng 80°C -> raw 1920 = 0x0780. Constant waveform pins the value.
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Constant, Offset = 80.0 },
        });
        node.AddPid(new Pid
        {
            Address = 0x5678,
            Name = "Rpm",
            Size = PidSize.Word,
            DataType = PidDataType.Unsigned,
            Scalar = 0.25,
            Offset = 0.0,
            Unit = "RPM",
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Constant, Offset = 2000.0 },
        });
        return node;
    }
}
