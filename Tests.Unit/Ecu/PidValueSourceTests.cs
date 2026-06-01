using Common.Protocol;
using Common.Signals;
using Common.Waveforms;
using Core.Ecu;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// The value-source model: the Signal column is the single selector for where a $22/$2D row's value comes from.
// "(none)" reads a flat 0 (no implicit waveform fallback), "Waveform" runs the row's generator, and assigning a
// signal selects the live engine model. StaticBytes still wins over the waveform but not over an attached signal.
public sealed class PidValueSourceTests
{
    private static Pid WaveformPid() => new()
    {
        Mode = PidMode.Mode22,
        Address = 0x2000,
        Size = PidSize.Word,
        DataType = PidDataType.Unsigned,
        // A flat constant so the assertion is deterministic regardless of timeMs.
        WaveformConfig = new WaveformConfig { Shape = WaveformShape.Constant, Offset = 42 },
    };

    [Fact]
    public void NoneSource_NoStaticBytes_ReadsZero()
    {
        var node = NodeFactory.CreateNode();
        var pid = WaveformPid();
        pid.ValueSource = PidValueSource.None;   // the editor's default for a freshly added row
        node.AddPid(pid);

        Assert.Equal(0, pid.SampleValue(timeMs: 1234));

        var buf = new byte[2];
        pid.WriteResponseBytes(timeMs: 1234, buf);
        Assert.Equal(new byte[] { 0x00, 0x00 }, buf);
    }

    [Fact]
    public void WaveformSource_RunsGenerator()
    {
        var node = NodeFactory.CreateNode();
        var pid = WaveformPid();   // default ValueSource is Waveform
        Assert.Equal(PidValueSource.Waveform, pid.ValueSource);
        node.AddPid(pid);

        Assert.Equal(42, pid.SampleValue(timeMs: 1234));

        var buf = new byte[2];
        pid.WriteResponseBytes(timeMs: 1234, buf);
        Assert.Equal(new byte[] { 0x00, 0x2A }, buf);   // 42 = 0x2A, unity scaling
    }

    [Fact]
    public void AssigningSignal_AutoSelectsSignalSource()
    {
        var pid = WaveformPid();
        Assert.Equal(PidValueSource.Waveform, pid.ValueSource);

        pid.Signal = SignalId.EngineRpm;

        Assert.Equal(PidValueSource.Signal, pid.ValueSource);
    }

    [Fact]
    public void NoneSource_WithStaticBytes_ReturnsStaticBytes()
    {
        // A bin-extracted placeholder carries StaticBytes with source None - it must still serve those bytes, not 0.
        var node = NodeFactory.CreateNode();
        var pid = new Pid
        {
            Mode = PidMode.Mode22,
            Address = 0x2100,
            LengthBytes = 2,
            ValueSource = PidValueSource.None,
            StaticBytes = new byte[] { 0xAB, 0xCD },
        };
        node.AddPid(pid);

        var buf = new byte[2];
        pid.WriteResponseBytes(timeMs: 0, buf);
        Assert.Equal(new byte[] { 0xAB, 0xCD }, buf);
    }
}
