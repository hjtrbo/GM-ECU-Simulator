using Common.IsoTp;
using Common.PassThru;
using Shim.IsoTp;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// Tests for the per-J2534-channel TP context. We capture bus egress in a
// recording delegate so the wiring (FC after FF, CF emissions on inbound
// FC drives, reassembled payload landing on the IsoChannel queue) is
// observable without a full bus + ECU stand-in.
public class Iso15765ChannelTests
{
    private static byte[] BeId(uint canId) => new byte[]
    {
        (byte)((canId >> 24) & 0xFF),
        (byte)((canId >> 16) & 0xFF),
        (byte)((canId >> 8) & 0xFF),
        (byte)(canId & 0xFF),
    };

    private static byte[] BeIdFrame(uint canId, byte[] data)
    {
        var buf = new byte[4 + data.Length];
        BeId(canId).CopyTo(buf, 0);
        data.CopyTo(buf, 4);
        return buf;
    }

    private static Iso15765Channel.IsoFilter NormalFilter(uint pattern, uint flowCtl, uint mask = 0xFFFFFFFF, uint id = 1)
        => new()
        {
            Id = id,
            MaskCanId = mask,
            PatternCanId = pattern,
            FlowCtlCanId = flowCtl,
            Format = AddressFormat.Normal,
        };

    // -----------------------------------------------------------------------
    // Filter management
    // -----------------------------------------------------------------------

    [Fact]
    public void AddFilter_then_MatchesInbound_routes_frames_to_TP_layer()
    {
        var ch = new Iso15765Channel(new IsoTpTimingParameters());
        ch.AddFilter(NormalFilter(pattern: 0x7E8, flowCtl: 0x7E0));

        var matched = ch.OnInboundCanFrame(0x7E8, new byte[] { 0x03, 0x62, 0xF1, 0x90 });
        Assert.True(matched);

        Assert.True(ch.ReassembledPayloadQueue.TryDequeue(out var msg));
        Assert.Equal(ProtocolID.ISO15765, msg!.ProtocolID);
        Assert.Equal(BeIdFrame(0x7E8, new byte[] { 0x62, 0xF1, 0x90 }), msg.Data);
    }

    [Fact]
    public void Inbound_frame_not_matching_any_filter_returns_false()
    {
        var ch = new Iso15765Channel(new IsoTpTimingParameters());
        ch.AddFilter(NormalFilter(pattern: 0x7E8, flowCtl: 0x7E0));

        var matched = ch.OnInboundCanFrame(0x123, new byte[] { 0x03, 0x01, 0x02, 0x03 });
        Assert.False(matched);
        Assert.True(ch.ReassembledPayloadQueue.IsEmpty);
    }

    [Fact]
    public void RemoveFilter_unroutes_subsequent_frames()
    {
        var ch = new Iso15765Channel(new IsoTpTimingParameters());
        ch.AddFilter(NormalFilter(pattern: 0x7E8, flowCtl: 0x7E0, id: 42));
        Assert.True(ch.RemoveFilter(42));
        Assert.False(ch.OnInboundCanFrame(0x7E8, new byte[] { 0x03, 0x62, 0xF1, 0x90 }));
    }

    // -----------------------------------------------------------------------
    // Inbound FF emits an FC.CTS via BusEgress, with FlowCtlCanId
    // -----------------------------------------------------------------------

    [Fact]
    public void Inbound_FF_emits_FC_via_BusEgress_targeted_at_FlowCtlCanId()
    {
        var emitted = new List<byte[]>();
        var ch = new Iso15765Channel(new IsoTpTimingParameters
        {
            BlockSizeSend = 0x10,
            StMinSendRaw = 0x05,
        })
        {
            BusEgress = frame => emitted.Add(frame),
        };
        ch.AddFilter(NormalFilter(pattern: 0x7E8, flowCtl: 0x7E0));

        // FF: total 20 bytes, first 6 in frame.
        ch.OnInboundCanFrame(0x7E8, new byte[] { 0x10, 20, 1, 2, 3, 4, 5, 6 });

        Assert.Single(emitted);
        var fcFrame = emitted[0];
        // [4 BE CAN_ID = 0x7E0][PCI 0x30][BS 0x10][STmin 0x05]
        Assert.Equal(BeIdFrame(0x7E0, new byte[] { 0x30, 0x10, 0x05 }), fcFrame);
    }

    // -----------------------------------------------------------------------
    // Inbound FF + CFs: reassembled payload lands on the IsoChannel queue
    // -----------------------------------------------------------------------

    [Fact]
    public void Inbound_FF_plus_CFs_reassembles_to_user_payload_via_queue()
    {
        var ch = new Iso15765Channel(new IsoTpTimingParameters());
        ch.BusEgress = _ => { };
        ch.AddFilter(NormalFilter(pattern: 0x7E8, flowCtl: 0x7E0));

        ch.OnInboundCanFrame(0x7E8, new byte[] { 0x10, 20, 1, 2, 3, 4, 5, 6 });
        ch.OnInboundCanFrame(0x7E8, new byte[] { 0x21, 7, 8, 9, 10, 11, 12, 13 });
        ch.OnInboundCanFrame(0x7E8, new byte[] { 0x22, 14, 15, 16, 17, 18, 19, 20 });

        Assert.True(ch.ReassembledPayloadQueue.TryDequeue(out var msg));
        Assert.Equal(BeIdFrame(0x7E8, Enumerable.Range(1, 20).Select(i => (byte)i).ToArray()), msg!.Data);
    }

    // -----------------------------------------------------------------------
    // Outbound TX with a single SF: BeginTransmit returns SF and TX result is N_OK
    // -----------------------------------------------------------------------

    [Fact]
    public void BeginTransmit_short_payload_returns_SF_and_N_OK()
    {
        var ch = new Iso15765Channel(new IsoTpTimingParameters());
        ch.AddFilter(NormalFilter(pattern: 0x7E8, flowCtl: 0x7E0));

        var begin = ch.BeginTransmit(0x7E0, new byte[] { 0x22, 0xF1, 0x90 });

        Assert.True(begin.Started);
        Assert.NotNull(begin.CanFrame);
        // [4 BE CAN_ID][SF PCI 0x03][22 F1 90]
        Assert.Equal(BeIdFrame(0x7E0, new byte[] { 0x03, 0x22, 0xF1, 0x90 }), begin.CanFrame);
        Assert.Equal(NextStep.Done, begin.Next);
        Assert.Equal(NResult.N_OK, ch.GetTxResult(begin.Filter!));
    }

    [Fact]
    public void BeginTransmit_with_no_matching_filter_returns_NoFilter()
    {
        var ch = new Iso15765Channel(new IsoTpTimingParameters());

        var begin = ch.BeginTransmit(0x7E0, new byte[] { 0x22, 0xF1, 0x90 });
        Assert.False(begin.Started);
        Assert.Null(begin.CanFrame);
    }

    // -----------------------------------------------------------------------
    // Outbound TX with FF: subsequent inbound FC drives the cascade
    // -----------------------------------------------------------------------

    [Fact]
    public void Outbound_FF_then_inbound_FC_CTS_drives_remaining_CFs_through_BusEgress()
    {
        var emitted = new List<byte[]>();
        var ch = new Iso15765Channel(new IsoTpTimingParameters())
        {
            BusEgress = frame => emitted.Add(frame),
        };
        ch.AddFilter(NormalFilter(pattern: 0x7E8, flowCtl: 0x7E0));

        // 20-byte payload -> FF + 2 CFs.
        var payload = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
        var begin = ch.BeginTransmit(0x7E0, payload);
        // Caller is expected to dispatch the FF themselves; record it manually
        // so the egressed list reflects the wire ordering.
        emitted.Add(begin.CanFrame!);

        // Inbound FC.CTS, BS=0, STmin=0 -> drain all remaining CFs back-to-back.
        ch.OnInboundCanFrame(0x7E8, new byte[] { 0x30, 0x00, 0x00 });

        // Expect: FF (we added it), then CF1 (SN=1), CF2 (SN=2).
        Assert.Equal(3, emitted.Count);
        Assert.Equal(0x10, emitted[0][4]);                  // FF
        Assert.Equal(0x21, emitted[1][4]);                  // CF1
        Assert.Equal(0x22, emitted[2][4]);                  // CF2 (final)
        Assert.Equal(NResult.N_OK, ch.GetTxResult(begin.Filter!));
    }

    // -----------------------------------------------------------------------
    // Outbound TX: inbound FC.OVFLW aborts with N_BUFFER_OVFLW
    // -----------------------------------------------------------------------

    [Fact]
    public void Outbound_FF_then_FC_OVFLW_aborts_TX_with_N_BUFFER_OVFLW()
    {
        var ch = new Iso15765Channel(new IsoTpTimingParameters());
        ch.BusEgress = _ => { };
        ch.AddFilter(NormalFilter(pattern: 0x7E8, flowCtl: 0x7E0));

        var begin = ch.BeginTransmit(0x7E0, new byte[100]);
        ch.OnInboundCanFrame(0x7E8, new byte[] { 0x32, 0x00, 0x00 });   // FC.OVFLW

        Assert.Equal(NResult.N_BUFFER_OVFLW, ch.GetTxResult(begin.Filter!));
    }
}
