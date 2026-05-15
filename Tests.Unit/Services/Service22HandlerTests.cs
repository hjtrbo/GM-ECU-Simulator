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
}
