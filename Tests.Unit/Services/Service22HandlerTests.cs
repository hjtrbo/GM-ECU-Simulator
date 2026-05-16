using Common.Protocol;
using Common.Waveforms;
using Core.Ecu;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// GMW3110-2010 §8.6 ReadDataByParameterIdentifier ($22) coverage.
// §8.6.1: include data for every supported PID, skip unsupported ones.
// §8.6.4 NRC $31: only when no requested PID is supported AND the request was
// physical. Functional request with no supported PIDs is silent.
public sealed class Service22HandlerTests
{
    private static EcuNode NodeWithPid(ushort address, byte fixedByte)
    {
        // Constant waveform whose sample IS the desired byte. Pid Scalar=1 /
        // Offset=0 makes ValueCodec.Encode round-trip the sample byte-for-byte.
        var node = NodeFactory.CreateNode();
        node.AddPid(new Pid
        {
            Address = address,
            Size = PidSize.Byte,
            DataType = PidDataType.Unsigned,
            Scalar = 1.0,
            Offset = 0.0,
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Constant, Offset = fixedByte },
        });
        return node;
    }

    [Fact]
    public void SingleSupportedPid_Physical_ReturnsPositiveResponse()
    {
        var node = NodeWithPid(0x000C, 0x42);
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22, 0x00, 0x0C }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { 0x62, 0x00, 0x0C, 0x42 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void MultiPid_PartiallySupported_ReturnsOnlySupportedSubset()
    {
        // §8.6.1: response includes data for the PIDs the ECU supports and
        // omits the unsupported ones. PID $000C is supported, $9999 is not.
        var node = NodeWithPid(0x000C, 0x99);
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22, 0x00, 0x0C, 0x99, 0x99 }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { 0x62, 0x00, 0x0C, 0x99 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void NoneSupported_Physical_ReturnsNrc31()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22, 0xFF, 0xAA, 0xFF, 0xBB }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.ReadDataByParameterIdentifier, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void NoneSupported_Functional_IsSilent()
    {
        // §8.6.4: functional request with no supported PIDs => no response.
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22, 0xFF, 0xAA }, ch, timeMs: 0, isFunctional: true);

        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void Functional_WithOneSupportedPid_StillResponds()
    {
        // §8.6.5.5: functional requests are valid; ECUs that support any of
        // the requested PIDs do reply.
        var node = NodeWithPid(0x000C, 0x07);
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22, 0x00, 0x0C }, ch, timeMs: 0, isFunctional: true);

        Assert.Equal(new byte[] { 0x62, 0x00, 0x0C, 0x07 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void MissingPid_ReturnsNrc12()
    {
        // §8.6.4: message_data_length < 3.
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22 }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.ReadDataByParameterIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void OddPidByteCount_ReturnsNrc12()
    {
        // §8.6.4: the number of bytes after the SID is not even.
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22, 0x00, 0x0C, 0xFF }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.ReadDataByParameterIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void MalformedLength_Functional_IsSilent()
    {
        // §8.6.4 + §6 convention: malformed-format errors on a functional
        // broadcast are silent (no NRC).
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22 }, ch, timeMs: 0, isFunctional: true);

        TestFrame.AssertEmpty(ch);
    }

    // ---- Long-PID + StaticBytes coverage ----
    //
    // Real GM ECUs expose $22 PIDs longer than 4 bytes (e.g. E38 PID 0x155B
    // is 17 bytes). The legacy PidSize enum caps at DWord; the LengthBytes
    // + StaticBytes pair lets us model these arbitrarily-sized PIDs with
    // bin-extracted placeholders.

    [Fact]
    public void Pid_With_LengthBytes17_AndStaticBytes_WritesAll17Bytes()
    {
        // PID 0x155B is the E38 OS-status block (17 bytes). Auto-extracted
        // PIDs in the simulator's config now carry an explicit LengthBytes
        // value and a zero-filled StaticBytes placeholder. Tested at the
        // Pid layer directly (a 17-byte $22 response triggers ISO-TP FF+CF
        // framing which is covered by ProgrammingSequenceTests; this test
        // pins the Pid-level encoding contract that Service22Handler hands
        // to the fragmenter).
        var pid = new Pid
        {
            Address = 0x155B,
            Size = PidSize.Byte,                       // legacy field; ignored when LengthBytes set
            DataType = PidDataType.Unsigned,
            Scalar = 1.0,
            LengthBytes = 17,
            StaticBytes = new byte[]
            {
                0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11
            },
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Constant },
        };

        Assert.Equal(17, pid.ResponseLength);
        var buf = new byte[17];
        pid.WriteResponseBytes(timeMs: 0, buf);

        Assert.Equal(new byte[] {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11,
        }, buf);
    }

    [Fact]
    public void Pid_With_StaticBytes_OverridesWaveform()
    {
        // Even if a non-trivial waveform is configured, StaticBytes wins -
        // the verbatim bytes are returned and the waveform pipeline is not
        // sampled. This is the model the bin auto-extractor uses to put a
        // zero placeholder in place without disturbing the waveform schema.
        var node = NodeFactory.CreateNode();
        node.AddPid(new Pid
        {
            Address = 0x2049,
            Size = PidSize.Byte,
            LengthBytes = 4,
            StaticBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            WaveformConfig = new WaveformConfig
            {
                Shape = WaveformShape.Sin, Amplitude = 1000, Offset = 500, FrequencyHz = 1,
            },
        });
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22, 0x20, 0x49 }, ch, timeMs: 12345, isFunctional: false);

        Assert.Equal(new byte[] { 0x62, 0x20, 0x49, 0xDE, 0xAD, 0xBE, 0xEF },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Pid_StaticBytes_ShorterThanLengthBytes_GetZeroPadded()
    {
        // A defensive case: if a config has fewer StaticBytes than the
        // declared LengthBytes, the response is padded out to the full
        // declared length with trailing zeros (Pid.WriteResponseBytes
        // contract). Helps tolerate hand-edits that trim trailing zero
        // bytes from a long hex string.
        var pid = new Pid
        {
            Address = 0x3000,
            Size = PidSize.Byte,
            LengthBytes = 8,
            StaticBytes = new byte[] { 0xAA, 0xBB },   // only 2 bytes; expect 6 zeros after
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Constant },
        };

        var buf = new byte[8];
        // Pre-fill with a sentinel so we can prove the unused tail is
        // explicitly zeroed (not just "left as default").
        buf.AsSpan().Fill(0xFF);
        pid.WriteResponseBytes(timeMs: 0, buf);

        Assert.Equal(new byte[] { 0xAA, 0xBB, 0, 0, 0, 0, 0, 0 }, buf);
    }

    [Fact]
    public void Pid_Without_NewFields_StillUsesWaveform_Regression()
    {
        // Regression: existing synthetic-waveform PIDs (no LengthBytes, no
        // StaticBytes) continue to be sampled via ValueCodec.Encode through
        // the legacy Size field. This mirrors SingleSupportedPid_Physical_
        // ReturnsPositiveResponse above but pins the regression-test intent
        // explicitly after the schema change.
        var node = NodeWithPid(0x000C, 0x42);
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22, 0x00, 0x0C }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { 0x62, 0x00, 0x0C, 0x42 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }
}
