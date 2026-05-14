using System.IO;
using Common.IsoTp;
using Common.PassThru;
using Core.Bus;
using Core.Ecu;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Shim.IsoTp;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// Coverage for the bootloader-capture mode added on top of $36. Two halves:
//   (1) Default (capture OFF) - Service36Handler still enforces GMW3110 §8.13.4
//       NRC $31 exactly, no .bin file is ever written. This is the "as per
//       spec" promise the user asked for when the tab checkbox is unticked.
//   (2) Capture ON - the same out-of-range request that fired NRC $31 in the
//       wire log now succeeds, EcuExitLogic dumps the assembled buffer, and
//       the file lands in the test's temp capture dir.
public class BootloaderCaptureTests
{
    private const ushort PhysReq = NodeFactory.PhysReq;
    private const ushort UsdtResp = NodeFactory.UsdtResp;

    private static (VirtualBus bus, EcuNode node, ChannelSession ch, Iso15765Channel iso)
        Wire()
    {
        var bus = new VirtualBus();
        var algo = new FakeSeedKeyAlgorithm();
        var node = NodeFactory.CreateNodeWithGenericModule(algo);
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.ISO15765, Baud = 500_000, Bus = bus };
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

    private static byte[] Send(Iso15765Channel iso, byte[] req)
    {
        var begin = iso.BeginTransmit(PhysReq, req);
        Assert.True(begin.Started);
        iso.BusEgress!(begin.CanFrame!);
        iso.EndTransmit(begin.Filter!);
        Assert.True(iso.ReassembledPayloadQueue.TryDequeue(out var msg));
        return msg!.Data.AsSpan(4).ToArray();
    }

    private static void SendNoResp(Iso15765Channel iso, byte[] req)
    {
        var begin = iso.BeginTransmit(PhysReq, req);
        Assert.True(begin.Started);
        iso.BusEgress!(begin.CanFrame!);
        iso.EndTransmit(begin.Filter!);
        iso.ReassembledPayloadQueue.TryDequeue(out _);
    }

    private static void DriveProgrammingPreconditions(Iso15765Channel iso)
    {
        Send(iso, [0x10, 0x03]);
        Send(iso, [0x28]);
        Send(iso, [0x27, 0x01]);
        Send(iso, [0x27, 0x02, 0xAB, 0xCD]);
        Send(iso, [0xA5, 0x01]);
        SendNoResp(iso, [0xA5, 0x03]);
    }

    /// <summary>
    /// Builds the exact $36 USDT payload shape observed in the user's wire log:
    /// SID + sub $00 + 3-byte address 0x003FB8 + 1025 bytes of data, totalling
    /// 1030 bytes. With the spec-mode bounds check this writes way past a
    /// 5344-byte declared buffer and trips NRC $31; with capture mode the
    /// payload lands at offset 0 of a freshly-rebased buffer and gets $76.
    /// </summary>
    private static byte[] BuildLargeAddressTransfer()
    {
        var payload = new byte[1030];
        payload[0] = 0x36;
        payload[1] = 0x00;       // sub = Download
        payload[2] = 0x00;
        payload[3] = 0x3F;
        payload[4] = 0xB8;       // 3-byte addr = 0x003FB8 = 16312
        for (int i = 5; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
        return payload;
    }

    [Fact]
    public void Capture_off_keeps_NRC_31_for_address_past_declared_size()
    {
        // The "as per spec" promise when the Capture Bootloader checkbox is
        // unticked - the same wire payload from the user's host must still
        // get NRC $31, identical to behaviour before the capture feature.
        var (bus, _, _, iso) = Wire();
        Assert.False(bus.Capture.BootloaderCaptureEnabled);

        DriveProgrammingPreconditions(iso);
        Assert.Equal(new byte[] { 0x74 }, Send(iso, [0x34, 0x00, 0x00, 0x14, 0xE0]));   // 5344 buffer

        Assert.Equal(new byte[] { 0x7F, 0x36, 0x31 }, Send(iso, BuildLargeAddressTransfer()));
    }

    [Fact]
    public void Capture_on_accepts_large_starting_address_and_returns_76()
    {
        // Same payload, capture toggle flipped - the handler rebases on the
        // first $36's address and stores everything relative to it.
        var (bus, node, _, iso) = Wire();
        bus.Capture.BootloaderCaptureEnabled = true;

        DriveProgrammingPreconditions(iso);
        Send(iso, [0x34, 0x00, 0x00, 0x14, 0xE0]);

        Assert.Equal(new byte[] { 0x76 }, Send(iso, BuildLargeAddressTransfer()));

        Assert.Equal(1025u, node.State.DownloadBytesReceived);
        Assert.Equal(0x003FB8u, node.State.DownloadCaptureBaseAddress);
        Assert.NotNull(node.State.DownloadBuffer);
        // The 1025 payload bytes (5..1029 of the request) landed at offset 0.
        Assert.Equal((byte)(5 & 0xFF), node.State.DownloadBuffer[0]);
        Assert.Equal((byte)(6 & 0xFF), node.State.DownloadBuffer[1]);
    }

    [Fact]
    public void Capture_on_dumps_buffer_to_disk_on_exit_to_normal()
    {
        var (bus, node, _, iso) = Wire();
        bus.Capture.BootloaderCaptureEnabled = true;
        var tmp = Path.Combine(Path.GetTempPath(), "GmEcuSimCapTest_" + Guid.NewGuid().ToString("N"));
        bus.Capture.CaptureDirectory = tmp;
        string? writtenPath = null;
        bus.Capture.CaptureWritten += p => writtenPath = p;

        try
        {
            DriveProgrammingPreconditions(iso);
            Send(iso, [0x34, 0x00, 0x00, 0x14, 0xE0]);
            Send(iso, BuildLargeAddressTransfer());

            // $20 ReturnToNormalMode triggers EcuExitLogic, which calls the
            // capture writer before ClearProgrammingState wipes the buffer.
            Send(iso, [0x20]);

            Assert.NotNull(writtenPath);
            Assert.True(File.Exists(writtenPath));
            var bytes = File.ReadAllBytes(writtenPath!);
            // File must contain the 1025-byte payload starting at offset 0.
            // We allow trailing zeros (the headroom-grown buffer is dumped whole)
            // but the first 1025 must match what we sent.
            Assert.True(bytes.Length >= 1025);
            for (int i = 0; i < 1025; i++)
                Assert.Equal((byte)((i + 5) & 0xFF), bytes[i]);

            // Filename embeds the base address and byte count for at-a-glance triage.
            string fname = Path.GetFileName(writtenPath!);
            Assert.Contains("003FB8", fname);
            Assert.Contains("1025", fname);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Capture_on_back_to_back_34s_produce_single_consolidated_file()
    {
        // 6Speed.T43's Pushspskernel issues two $34s, and the subsequent Sendbin
        // loop issues one $34 per ~4080-byte segment. In capture mode the user
        // expects ONE consolidated .bin per programming session, not one per $34.
        // The second $34 must preserve the existing buffer + base address; only
        // the declared size updates.
        var (bus, node, _, iso) = Wire();
        bus.Capture.BootloaderCaptureEnabled = true;
        var tmp = Path.Combine(Path.GetTempPath(), "GmEcuSimCapTest_" + Guid.NewGuid().ToString("N"));
        bus.Capture.CaptureDirectory = tmp;
        var writtenPaths = new List<string>();
        bus.Capture.CaptureWritten += p => writtenPaths.Add(p);

        try
        {
            DriveProgrammingPreconditions(iso);

            // First $34/$36 pair: declare 1024 bytes, write 1025. The 1025
            // bytes received >= 1024 declared marks the first download
            // logically complete (so the second $34 isn't rejected with
            // "download in progress"). Capture mode tolerates the overshoot.
            Send(iso, [0x34, 0x00, 0x04, 0x00]);
            Send(iso, BuildLargeAddressTransfer());
            var bufferAfterFirst = node.State.DownloadBuffer;
            uint? baseAfterFirst = node.State.DownloadCaptureBaseAddress;
            uint bytesAfterFirst = node.State.DownloadBytesReceived;

            // Second $34: NEW declared size (16 bytes), but the existing buffer
            // and base address must be preserved.
            Send(iso, [0x34, 0x00, 0x00, 0x00, 0x10]);
            Assert.Same(bufferAfterFirst, node.State.DownloadBuffer);
            Assert.Equal(baseAfterFirst, node.State.DownloadCaptureBaseAddress);
            Assert.Equal(bytesAfterFirst, node.State.DownloadBytesReceived);
            Assert.Equal(0x10u, node.State.DownloadDeclaredSize);

            // Second $36: writes 8 bytes at a slightly later absolute address.
            Send(iso, [0x36, 0x00, 0x00, 0x40, 0x00,
                       0xCA, 0xFE, 0xBA, 0xBE, 0x01, 0x02, 0x03, 0x04]);

            // Bytes accumulated across both segments.
            Assert.Equal(bytesAfterFirst + 8u, node.State.DownloadBytesReceived);

            // $20 ends the session - ONE file written, not two.
            Send(iso, [0x20]);

            Assert.Single(writtenPaths);
            Assert.True(File.Exists(writtenPaths[0]));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Capture_off_does_not_write_a_file_on_exit()
    {
        // Even when a successful (spec-shape) $36 has run, capture-off must
        // not touch the disk - the toggle is the sole opt-in.
        var (bus, _, _, iso) = Wire();
        Assert.False(bus.Capture.BootloaderCaptureEnabled);
        var tmp = Path.Combine(Path.GetTempPath(), "GmEcuSimCapTest_" + Guid.NewGuid().ToString("N"));
        bus.Capture.CaptureDirectory = tmp;

        DriveProgrammingPreconditions(iso);
        Send(iso, [0x34, 0x00, 0x00, 0x00, 0x10]);    // 16-byte declared buffer
        Send(iso, [0x36, 0x00, 0x00, 0x00, 0x00,
                   0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44]);  // valid spec-mode write
        Send(iso, [0x20]);

        Assert.False(Directory.Exists(tmp), "capture directory must not exist when capture is off");
    }
}
