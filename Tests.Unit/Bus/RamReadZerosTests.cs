using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Ecu.Personas;
using Core.Transport;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Bus;

// EcuNode.RamReadReturnsZeros: when ticked, a $23 ReadMemoryByAddress for an
// address beyond the loaded flash bin (RAM) is answered with a positive $63
// reply padded with zeros instead of NRC $31 RequestOutOfRange. The check
// lives in VirtualBus.DispatchUsdt before the persona dispatch, so it applies
// to every persona; in-bin reads still fall through to the persona.
//
// These drive the real inbound path (DispatchHostTx with a single ISO-TP frame)
// rather than calling the private DispatchUsdt directly. The RAM boundary reads
// the FordUdsPersona singleton's flash-bin size, so this class joins the
// FordUdsPersona collection and resets the bin around each test.
[Collection(FordUdsPersonaCollection.Name)]
public sealed class RamReadZerosTests
{
    // 23 <4-byte BE addr> <2-byte BE len> - the 7-byte ReadMemoryByAddress shape.
    private static byte[] ReadMemoryRequest(uint addr, ushort len) => new byte[]
    {
        0x23,
        (byte)(addr >> 24), (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr,
        (byte)(len >> 8), (byte)len,
    };

    private static (VirtualBus bus, EcuNode node, ChannelSession ch) Setup(
        IDiagnosticPersona persona, bool ramReadReturnsZeros)
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        node.Persona = persona;
        node.RamReadReturnsZeros = ramReadReturnsZeros;
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        return (bus, node, ch);
    }

    // Wraps a USDT payload (<= 7 bytes) in a single ISO-TP frame addressed to the
    // node's physical request CAN ID and feeds it through the real host-Tx path.
    private static void SendSingleFrame(VirtualBus bus, ChannelSession ch, byte[] usdt)
    {
        Assert.True(usdt.Length <= 7, "single-frame helper only carries up to 7 bytes");
        var frame = new byte[CanFrame.IdBytes + 1 + usdt.Length];
        CanFrame.WriteId(frame, NodeFactory.PhysReq);
        frame[CanFrame.IdBytes] = (byte)usdt.Length;        // SF PCI: nibble 0, length in low nibble
        usdt.CopyTo(frame, CanFrame.IdBytes + 1);
        bus.DispatchHostTx(frame, ch);
    }

    [Fact]
    public void Enabled_NoBin_RamRead_ReturnsPositiveZeros_OnAnyPersona()
    {
        // Default GM persona doesn't even handle $23 - the RAM fallback runs
        // before persona dispatch, so the read is answered regardless. No bin
        // loaded -> every address is RAM.
        FordUdsPersona.LoadFlashBin((byte[]?)null);
        var (bus, _, ch) = Setup(Gmw3110Persona.Instance, ramReadReturnsZeros: true);

        // The motivating example: addr=$003FA0D4, len=1.
        SendSingleFrame(bus, ch, ReadMemoryRequest(0x003FA0D4, 1));

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x63, 0x00 }, resp);
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void Enabled_RamRead_PadsRequestedLengthWithZeros()
    {
        FordUdsPersona.LoadFlashBin((byte[]?)null);
        var (bus, _, ch) = Setup(Gmw3110Persona.Instance, ramReadReturnsZeros: true);

        SendSingleFrame(bus, ch, ReadMemoryRequest(0x003FA0D4, 4));

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x63, 0x00, 0x00, 0x00, 0x00 }, resp);
    }

    [Fact]
    public void Enabled_InBinRead_FallsThroughToPersona_ServesRealBytes()
    {
        // With the Ford persona and a bin loaded, an in-bin address must still
        // read the real bytes - the RAM fallback only covers addresses past the
        // bin length.
        var bin = new byte[0x20000];
        System.Text.Encoding.ASCII.GetBytes("6FPA", 0, 4, bin, 0x100C0);
        FordUdsPersona.LoadFlashBin(bin);
        try
        {
            var (bus, _, ch) = Setup(FordUdsPersona.Instance, ramReadReturnsZeros: true);

            SendSingleFrame(bus, ch, ReadMemoryRequest(0x000100C0, 4));

            var resp = TestFrame.DequeueSingleFrameUsdt(ch);
            Assert.Equal(new byte[] { 0x63, 0x36, 0x46, 0x50, 0x41 }, resp); // "6FPA"
        }
        finally
        {
            FordUdsPersona.LoadFlashBin((byte[]?)null);
        }
    }

    [Fact]
    public void Disabled_RamRead_StillNrcsRequestOutOfRange()
    {
        // Flag off (the default): the Ford persona's spec-correct NRC $31 stands
        // for an out-of-range read - exactly the behaviour the example shows.
        var bin = new byte[16];
        FordUdsPersona.LoadFlashBin(bin);
        try
        {
            var (bus, _, ch) = Setup(FordUdsPersona.Instance, ramReadReturnsZeros: false);

            SendSingleFrame(bus, ch, ReadMemoryRequest(0x003FA0D4, 1));

            var resp = TestFrame.DequeueSingleFrameUsdt(ch);
            Assert.Equal(new byte[] { 0x7F, 0x23, 0x31 }, resp); // RequestOutOfRange
        }
        finally
        {
            FordUdsPersona.LoadFlashBin((byte[]?)null);
        }
    }
}
