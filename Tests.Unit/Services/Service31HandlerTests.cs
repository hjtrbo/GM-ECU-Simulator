using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Services.Uds;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// ISO 14229-1 §11.7 RoutineControl coverage. Not a GMW3110 service; see
// Core/Services/Uds/Service31Handler.cs header for why the simulator
// implements it (SPS kernel compatibility for powerpcm_flasher and similar
// tools). The handler lives under UdsKernelPersona at the dispatcher level;
// these tests call the handler directly, so persona state is not exercised.
public sealed class Service31HandlerTests
{
    private const byte Sid = 0x31;
    private const byte PosSid = 0x71;

    [Fact]
    public void Start_EraseRoutine_Unlocked_ReturnsPositiveEchoWithStatus00()
    {
        // powerpcm "erase calib": $31 $01 $FF $00 + startAddr(4) + size(4).
        // Wire bytes captured from bus_20260515_155328.csv:83 / CF#1:85.
        var node = NodeFactory.CreateNode();
        node.State.SecurityUnlockedLevel = 1;
        var ch = NodeFactory.CreateChannel();

        bool ok = Service31Handler.Handle(node,
            new byte[] { Sid, 0x01, 0xFF, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00 },
            ch);

        Assert.True(ok);
        Assert.Equal(new byte[] { PosSid, 0x01, 0xFF, 0x00, 0x00 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Start_CheckRoutine_NoRegionCaptured_ReturnsKernelShapeWithCrcZero()
    {
        // powerpcm "validating download": $31 $01 $04 $01 + addr(4) + size(4).
        // Wire bytes from bus_20260515_155328.csv:2653 / CF#1:2655. With no
        // captured flash region in scope, the handler falls back to CRC=$0000
        // so the tester prints "NOT valid!" and exits its 30-poll loop instead
        // of hanging the UI. Capture mode is off here.
        var node = NodeFactory.CreateNode();
        node.State.SecurityUnlockedLevel = 1;
        var ch = NodeFactory.CreateChannel();

        bool ok = Service31Handler.Handle(node,
            new byte[] { Sid, 0x01, 0x04, 0x01, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00 },
            ch);

        Assert.True(ok);
        Assert.Equal(new byte[] { PosSid, 0x04, 0x00, 0x00 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Start_CheckRoutine_RegionContainsRange_ReturnsCrcOfMirrorBytes()
    {
        // Region declared by an earlier $31 $FF00 erase mirrors $36 writes.
        // CheckMemory must compute CRC-16/CCITT-FALSE over the requested slice
        // of the region buffer - the actual bytes the tester wrote, not the
        // simulator's loaded bin image.
        var node = NodeFactory.CreateNode();
        node.State.SecurityUnlockedLevel = 1;
        var region = new Core.Ecu.FlashEraseRegion(0x001C0000u, 0x100u);
        node.State.CapturedFlashRegions.Add(region);
        for (int i = 0; i < region.Buffer.Length; i++)
            region.Buffer[i] = (byte)i;
        ushort expected = Common.Protocol.Crc16Ccitt.Compute(region.Buffer);

        var ch = ChannelWithCaptureOn();
        bool ok = Service31Handler.Handle(node,
            new byte[] { Sid, 0x01, 0x04, 0x01, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00 },
            ch);

        Assert.True(ok);
        Assert.Equal(new byte[] { PosSid, 0x04, (byte)(expected >> 8), (byte)(expected & 0xFF) },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Start_CheckRoutine_RangePartiallyOutsideRegion_ReturnsCrcZero()
    {
        // A range that spills past the declared region must NOT silently
        // fabricate $FF bytes for the uncovered tail - that would emit a
        // confident-looking CRC for memory the kernel never erased.
        var node = NodeFactory.CreateNode();
        node.State.SecurityUnlockedLevel = 1;
        node.State.CapturedFlashRegions.Add(new Core.Ecu.FlashEraseRegion(0x001C0000u, 0x80u));
        var ch = ChannelWithCaptureOn();

        // Request length 0x100 = 256B, region is only 0x80 = 128B.
        bool ok = Service31Handler.Handle(node,
            new byte[] { Sid, 0x01, 0x04, 0x01, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00 },
            ch);

        Assert.True(ok);
        Assert.Equal(new byte[] { PosSid, 0x04, 0x00, 0x00 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Theory]
    [InlineData((byte)0x02)] // stopRoutine
    [InlineData((byte)0x03)] // requestRoutineResults
    public void OtherValidSubs_Unlocked_ReturnPositiveEchoWithStatus00(byte sub)
    {
        var node = NodeFactory.CreateNode();
        node.State.SecurityUnlockedLevel = 1;
        var ch = NodeFactory.CreateChannel();

        bool ok = Service31Handler.Handle(node, new byte[] { Sid, sub, 0xFF, 0x00 }, ch);

        Assert.True(ok);
        Assert.Equal(new byte[] { PosSid, sub, 0xFF, 0x00, 0x00 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Locked_ReturnsNrc33()
    {
        // §11.7.4 / GMW3110 §7.2 Nrc $33: kernel routines are post-$27.
        var node = NodeFactory.CreateNode();
        Assert.Equal(0, node.State.SecurityUnlockedLevel);
        var ch = NodeFactory.CreateChannel();

        bool ok = Service31Handler.Handle(node,
            new byte[] { Sid, 0x01, 0xFF, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00 },
            ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Sid, Nrc.SecurityAccessDenied },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x04)]
    [InlineData((byte)0x7F)]
    [InlineData((byte)0xFF)]
    public void UndefinedSub_ReturnsNrc12(byte sub)
    {
        var node = NodeFactory.CreateNode();
        node.State.SecurityUnlockedLevel = 1;
        var ch = NodeFactory.CreateChannel();

        bool ok = Service31Handler.Handle(node, new byte[] { Sid, sub, 0xFF, 0x00 }, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Sid, Nrc.SubFunctionNotSupportedInvalidFormat },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Theory]
    [InlineData(new byte[] { Sid })]                              // SID only
    [InlineData(new byte[] { Sid, 0x01 })]                        // sub only
    [InlineData(new byte[] { Sid, 0x01, 0xFF })]                  // routineId truncated to 1 byte
    public void TooShort_ReturnsNrc12(byte[] payload)
    {
        var node = NodeFactory.CreateNode();
        node.State.SecurityUnlockedLevel = 1;
        var ch = NodeFactory.CreateChannel();

        bool ok = Service31Handler.Handle(node, payload, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Sid, Nrc.SubFunctionNotSupportedInvalidFormat },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    private static ChannelSession ChannelWithCaptureOn()
    {
        var bus = new VirtualBus();
        bus.Capture.BootloaderCaptureEnabled = true;
        return new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
    }

    [Fact]
    public void Start_EraseFF00_CaptureOn_RecordsRegion()
    {
        var node = NodeFactory.CreateNode();
        node.State.SecurityUnlockedLevel = 1;
        var ch = ChannelWithCaptureOn();

        bool ok = Service31Handler.Handle(node,
            new byte[] { Sid, 0x01, 0xFF, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00 },
            ch);

        Assert.True(ok);
        var region = Assert.Single(node.State.CapturedFlashRegions);
        Assert.Equal(0x001C0000u, region.StartAddress);
        Assert.Equal(0x00040000u, region.Size);
        Assert.Equal(0x40000, region.Buffer.Length);
        // Buffer must be initialized to $FF to match post-erase flash.
        Assert.All(region.Buffer, b => Assert.Equal(0xFF, b));
        Assert.Equal(0u, region.BytesWritten);
    }

    [Fact]
    public void Start_EraseFF00_CaptureOff_DoesNotRecordRegion()
    {
        // Region tracking is gated on BootloaderCaptureEnabled so we don't
        // allocate a multi-KiB buffer per erase when the user isn't asking
        // for flash dumps.
        var node = NodeFactory.CreateNode();
        node.State.SecurityUnlockedLevel = 1;
        var bus = new VirtualBus();
        Assert.False(bus.Capture.BootloaderCaptureEnabled);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };

        bool ok = Service31Handler.Handle(node,
            new byte[] { Sid, 0x01, 0xFF, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00 },
            ch);

        Assert.True(ok);
        Assert.Empty(node.State.CapturedFlashRegions);
    }

    [Fact]
    public void Start_NonErase_DoesNotRecordRegion()
    {
        var node = NodeFactory.CreateNode();
        node.State.SecurityUnlockedLevel = 1;
        var ch = ChannelWithCaptureOn();

        // $0401 Check: same option-record shape but it's not Erase.
        Service31Handler.Handle(node,
            new byte[] { Sid, 0x01, 0x04, 0x01, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00 },
            ch);

        Assert.Empty(node.State.CapturedFlashRegions);
    }

    [Fact]
    public void Start_EraseFF00_OversizeRegion_ReturnsNrc31_AndDoesNotRecord()
    {
        // size = 0x80000000 (2 GiB) exceeds MaxFlashEraseRegionBytes and would
        // OOM the simulator if accepted - must be rejected and not recorded.
        var node = NodeFactory.CreateNode();
        node.State.SecurityUnlockedLevel = 1;
        var ch = ChannelWithCaptureOn();

        bool ok = Service31Handler.Handle(node,
            new byte[] { Sid, 0x01, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00 },
            ch);

        Assert.False(ok);
        Assert.Empty(node.State.CapturedFlashRegions);
        Assert.Equal(new byte[] { Service.NegativeResponse, Sid, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }
}
