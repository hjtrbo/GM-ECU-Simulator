using Common.PassThru;
using Common.Persistence;
using Common.Protocol;
using Common.Waveforms;
using Core.Bus;
using Core.Ecu;
using Core.Persistence;
using Core.Services;

namespace EcuSimulator.Tests.Core;

// Pid.Address is uint so simulator PIDs can sit at 32-bit memory addresses
// like 0x002C0000. The wire format for $2D supports 2/3/4-byte addresses;
// $22 still uses a 16-bit PID id. These tests pin both paths.
public class PidAddress32BitTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    private static EcuNode BuildEcuWithMemoryPid()
    {
        var node = new EcuNode
        {
            Name = "ECM",
            PhysicalRequestCanId = 0x241,
            UsdtResponseCanId = 0x641,
            UudtResponseCanId = 0x541,
        };
        // Static PID at a 24-bit memory address — only reachable via $2D
        // because the address is outside the 16-bit $22 PID id range.
        node.AddPid(new Pid
        {
            Address = 0x002C0000,
            Name = "Some RAM cell",
            Size = PidSize.Word,
            DataType = PidDataType.Unsigned,
            Scalar = 1.0,
            Offset = 0.0,
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Constant, Offset = 4242 },
        });
        return node;
    }

    [Fact]
    public void GetPid_FindsByThirtyTwoBitAddress()
    {
        var node = BuildEcuWithMemoryPid();
        var pid = node.GetPid(0x002C0000u);
        Assert.NotNull(pid);
        Assert.Equal(0x002C0000u, pid!.Address);
    }

    [Fact]
    public void GetPid_DoesNotConfuseTruncatedAddress()
    {
        var node = BuildEcuWithMemoryPid();
        // Bug guard: the old `(ushort)0x002C0000` cast collapsed to 0x0000.
        // A lookup at the truncated address must NOT find the 32-bit PID.
        Assert.Null(node.GetPid(0x0000u));
    }

    [Fact]
    public void Service2D_With4ByteAddress_MapsTo32BitPid()
    {
        var node = BuildEcuWithMemoryPid();
        var ch = NewChannel();

        // SID 0x2D | new PID id 0xFE40 | MA 0x002C0000 (4 bytes) | MS 2
        Service2DHandler.Handle(
            node,
            [0x2D, 0xFE, 0x40, 0x00, 0x2C, 0x00, 0x00, 0x02],
            ch);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        // Positive $2D response: 0x6D + PID id echo (no NRC)
        Assert.Equal(0x6D, msg!.Data[5]);
        Assert.Equal(0xFE, msg.Data[6]);
        Assert.Equal(0x40, msg.Data[7]);

        // The new short-PID is reachable by $22 and produces the same
        // engineering value as the 32-bit source.
        var shortPid = node.GetPid(0xFE40u);
        var memPid = node.GetPid(0x002C0000u);
        Assert.NotNull(shortPid);
        Assert.NotNull(memPid);
        Assert.Equal(
            memPid!.Waveform.Sample(0),
            shortPid!.Waveform.Sample(0));
    }

    [Fact]
    public void Service2D_4ByteAddress_OverIsoTp_MatchesSpecExample()
    {
        // GMW3110 §8.11.5.1 worked example (Table 129):
        //   T(USDT-FF) $241  $10 $08 $2D $CC $FF $00 $01 $11
        //   N(USDT-FC) $641  $30 $00 $00
        //   T(USDT-CF) $241  $21 $02 $02
        //   N(USDT-SF) $641  $03 $6D $CC $FF
        // — 4-byte memoryAddress = 0x00011102, memorySize = 0x02, PID = 0xCCFF.
        var bus = new VirtualBus();
        var node = BuildEcuWithMemoryPid();
        // Override the static PID address to match the spec example so the
        // request finds something to mirror.
        node.GetPid(0x002C0000)!.Address = 0x00011102;
        bus.AddNode(node);

        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

        // First Frame: total length 8, SID 0x2D, PID 0xCCFF, MA hi/mid bytes.
        bus.DispatchHostTx(
            [0x00, 0x00, 0x02, 0x41, 0x10, 0x08, 0x2D, 0xCC, 0xFF, 0x00, 0x01, 0x11],
            ch);
        // The bus emits a Flow Control frame back per ISO 15765-2.
        Assert.True(ch.RxQueue.TryDequeue(out var fc));
        Assert.Equal(0x30, fc!.Data[4]);                       // CTS

        // Consecutive Frame: seq 1, MA low byte, then memorySize.
        bus.DispatchHostTx(
            [0x00, 0x00, 0x02, 0x41, 0x21, 0x02, 0x02],
            ch);

        // Positive response: SF len 3, $6D, PID echo $CC $FF.
        Assert.True(ch.RxQueue.TryDequeue(out var resp));
        Assert.Equal(0x03, resp!.Data[4]);
        Assert.Equal(0x6D, resp.Data[5]);
        Assert.Equal(0xCC, resp.Data[6]);
        Assert.Equal(0xFF, resp.Data[7]);
        Assert.NotNull(node.GetPid(0xCCFFu));
    }

    [Fact]
    public void HexUIntConverter_RoundTripsLargeAddresses()
    {
        var cfg = new SimulatorConfig
        {
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
                            Address = 0x002C0000u,
                            Name = "RAM",
                            Size = PidSize.Word,
                            DataType = PidDataType.Unsigned,
                            Waveform = new WaveformDto { Shape = WaveformShape.Sin, Amplitude = 1, FrequencyHz = 1 },
                        },
                    },
                },
            },
        };
        var json = ConfigSerializer.Serialize(cfg);
        Assert.Contains("\"0x002C0000\"", json);
        var round = ConfigSerializer.Deserialize(json);
        Assert.Equal(0x002C0000u, round.Ecus[0].Pids[0].Address);
    }

    [Fact]
    public void HexUIntConverter_AcceptsBareHexAndPlainDecimal()
    {
        // Sanity-check both input forms our user is likely to write in JSON.
        var json1 = """{ "version": 1, "ecus": [ { "name": "x", "physicalRequestCanId": "0x241", "usdtResponseCanId": "0x641", "uudtResponseCanId": "0x541", "pids": [ { "address": "2c0000h", "name": "n", "size": "Word", "dataType": "Unsigned", "waveform": { "shape": "Sin", "amplitude": 1, "frequencyHz": 1 } } ] } ] }""";
        var c1 = ConfigSerializer.Deserialize(json1);
        Assert.Equal(0x002C0000u, c1.Ecus[0].Pids[0].Address);

        var json2 = """{ "version": 1, "ecus": [ { "name": "x", "physicalRequestCanId": "0x241", "usdtResponseCanId": "0x641", "uudtResponseCanId": "0x541", "pids": [ { "address": 2883584, "name": "n", "size": "Word", "dataType": "Unsigned", "waveform": { "shape": "Sin", "amplitude": 1, "frequencyHz": 1 } } ] } ] }""";
        var c2 = ConfigSerializer.Deserialize(json2);
        Assert.Equal(2883584u, c2.Ecus[0].Pids[0].Address);  // 2883584 == 0x002C0000
    }
}
