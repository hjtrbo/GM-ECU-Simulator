using System.Text;
using Common.IsoTp;
using Common.PassThru;
using Common.Persistence;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Persistence;
using Core.Services;
using Core.Transport;
using EcuSimulator.Tests.TestHelpers;
using Shim.IsoTp;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// GMW3110-2010 §8.3 ReadDataByIdentifier ($1A) coverage.
//
// Handler-level tests exercise the validation and lookup logic against a
// directly-invoked Service1AHandler. The VIN ($90) integration test drives a
// full Iso15765Channel round-trip so the multi-frame response (FF + CFs) is
// exercised end-to-end like the J2534 host would see it.
public sealed class Service1AHandlerTests
{
    private const ushort PhysReq = NodeFactory.PhysReq;       // $7E0
    private const ushort UsdtResp = NodeFactory.UsdtResp;     // $7E8

    // ---------- direct handler tests ----------

    [Fact]
    public void Vin_DID_returns_positive_response_with_configured_bytes_SF_path()
    {
        // Short identifier ($5A + $99 + 4 BCD bytes = 6 bytes) fits in a Single Frame.
        var node = NodeFactory.CreateNode();
        node.SetIdentifier(0x99, new byte[] { 0x20, 0x26, 0x05, 0x14 });
        var ch = NodeFactory.CreateChannel();

        Service1AHandler.Handle(node, new byte[] { 0x1A, 0x99 }, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x5A, 0x99, 0x20, 0x26, 0x05, 0x14 }, resp);
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void Unknown_DID_returns_NRC_RequestOutOfRange()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service1AHandler.Handle(node, new byte[] { 0x1A, 0x90 }, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.ReadDataByIdentifier, Nrc.RequestOutOfRange }, resp);
    }

    [Fact]
    public void Missing_DID_byte_returns_NRC_SubFunctionNotSupportedInvalidFormat()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service1AHandler.Handle(node, new byte[] { 0x1A }, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.ReadDataByIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat }, resp);
    }

    [Fact]
    public void Extra_request_bytes_return_NRC_SubFunctionNotSupportedInvalidFormat()
    {
        // $1A only takes one DID. Multi-DID is $22, not $1A.
        var node = NodeFactory.CreateNode();
        node.SetIdentifier(0x90, Encoding.ASCII.GetBytes("1G1ZB5ST7HF000000"));
        var ch = NodeFactory.CreateChannel();

        Service1AHandler.Handle(node, new byte[] { 0x1A, 0x90, 0x92 }, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.ReadDataByIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat }, resp);
    }

    // ---------- end-to-end VIN through Iso15765Channel ----------

    [Fact]
    public void Vin_response_fragments_and_reassembles_over_Iso15765Channel()
    {
        var (_, _, _, iso) = SetupBusAndChannel(vin: "1G1ZB5ST7HF000000");

        var resp = SendAndReceive(iso, new byte[] { 0x1A, 0x90 });

        var expected = new byte[2 + 17];
        expected[0] = 0x5A;
        expected[1] = 0x90;
        Encoding.ASCII.GetBytes("1G1ZB5ST7HF000000").CopyTo(expected.AsSpan(2));
        Assert.Equal(expected, resp);
    }

    [Fact]
    public void Default_ECM_serves_VIN_via_DefaultEcuConfig()
    {
        // First-launch fallback must populate $1A $90 on the ECM. Catches a
        // regression where the default config gets stripped of the VIN.
        var cfg = DefaultEcuConfig.Build();
        var ecm = cfg.Ecus.FirstOrDefault(e => e.Name == "ECM");
        Assert.NotNull(ecm);
        var vin = ecm!.Identifiers?.FirstOrDefault(i => i.Did == 0x90);
        Assert.NotNull(vin);
        Assert.Equal(17, (vin!.Ascii?.Length ?? 0));
    }

    [Fact]
    public void Config_round_trip_preserves_ASCII_and_Hex_identifier_values()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        node.SetIdentifier(0x90, Encoding.ASCII.GetBytes("1G1ZB5ST7HF000000"));
        node.SetIdentifier(0x99, new byte[] { 0x20, 0x26, 0x05, 0x14 });
        bus.AddNode(node);

        var snapshot = ConfigStore.Snapshot(bus);
        var json = ConfigSerializer.Serialize(snapshot);
        var reloaded = ConfigSerializer.Deserialize(json);
        var newBus = new VirtualBus();
        ConfigStore.ApplyTo(reloaded, newBus);

        var rehydrated = newBus.Nodes.Single();
        Assert.Equal(Encoding.ASCII.GetBytes("1G1ZB5ST7HF000000"), rehydrated.GetIdentifier(0x90));
        Assert.Equal(new byte[] { 0x20, 0x26, 0x05, 0x14 }, rehydrated.GetIdentifier(0x99));

        // VIN should serialise as Ascii (all printable), BCD as Hex.
        var ecuDto = snapshot.Ecus.Single();
        Assert.NotNull(ecuDto.Identifiers);
        var vinDto = ecuDto.Identifiers!.Single(i => i.Did == 0x90);
        Assert.Equal("1G1ZB5ST7HF000000", vinDto.Ascii);
        Assert.Null(vinDto.Hex);
        var bcdDto = ecuDto.Identifiers!.Single(i => i.Did == 0x99);
        Assert.Null(bcdDto.Ascii);
        Assert.Equal("20 26 05 14", bcdDto.Hex);
    }

    // ---------- helpers ----------

    private static (VirtualBus bus, EcuNode node, ChannelSession ch, Iso15765Channel iso)
        SetupBusAndChannel(string vin)
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        node.SetIdentifier(0x90, Encoding.ASCII.GetBytes(vin));
        bus.AddNode(node);

        var ch = new ChannelSession
        {
            Id = 1,
            Protocol = ProtocolID.ISO15765,
            Baud = 500_000,
            Bus = bus,
        };
        var iso = new Iso15765Channel(new IsoTpTimingParameters());
        iso.BusEgress = frame => bus.DispatchHostTx(frame, ch);
        ch.IsoChannel = iso;
        ch.IsoChannelInbound = (canId, frame) => iso.OnInboundCanFrame(canId, frame.AsSpan(4));

        iso.AddFilter(new Iso15765Channel.IsoFilter
        {
            Id = 1,
            MaskCanId = 0xFFFFFFFF,
            PatternCanId = UsdtResp,
            FlowCtlCanId = PhysReq,
            Format = AddressFormat.Normal,
        });

        return (bus, node, ch, iso);
    }

    private static byte[] SendAndReceive(Iso15765Channel iso, byte[] request)
    {
        var begin = iso.BeginTransmit(PhysReq, request);
        Assert.True(begin.Started, "BeginTransmit failed");
        iso.BusEgress!(begin.CanFrame!);
        iso.EndTransmit(begin.Filter!);

        Assert.True(iso.ReassembledPayloadQueue.TryDequeue(out var msg),
            $"no response for SID 0x{request[0]:X2}");
        return msg!.Data.AsSpan(4).ToArray();
    }
}
