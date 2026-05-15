using System.IO;
using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Scheduler;
using Core.Services;
using Core.Services.Uds;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// End-to-end coverage for the SPS-kernel flash-capture feature:
//   $31 sub $01 routineId $FF00 declares an erase region -> NodeState records
//     a $FF-filled FlashEraseRegion buffer
//   $36 writes whose [start, end) sit inside the region mirror into the
//     region buffer (in addition to the per-$36 fragment .bin)
//   EcuExitLogic flushes one consolidated .bin per region at session end
//
// The handler-level $31 region-recording, $31 NRCs, etc. live in
// Service31HandlerTests; this file is the round-trip mirroring + file dump.
public sealed class FlashRegionCaptureTests
{
    private static (VirtualBus bus, EcuNode node, ChannelSession ch) Wire(bool captureOn)
    {
        var bus = new VirtualBus();
        bus.Capture.BootloaderCaptureEnabled = captureOn;
        var node = NodeFactory.CreateNode();
        // Match the powerpcm wire log: kernel sends absolute 4-byte addresses.
        node.DownloadAddressByteCount = 4;
        // Bring the node up to "post-$27 programming session" without driving
        // the full handler chain - these tests focus on $31/$36/exit wiring,
        // not on the upstream $10/$28/$27/$A5 sequence.
        node.State.SecurityUnlockedLevel = 1;
        node.State.NormalCommunicationDisabled = true;
        node.State.ProgrammingModeRequested = true;
        node.State.ProgrammingModeActive = true;
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        // EcuExitLogic falls back to LastEnhancedChannel when respondOn is null,
        // and our $20 test path passes the channel explicitly. Set this anyway
        // so the P3C-timeout variant of the same flush works too.
        node.State.LastEnhancedChannel = ch;
        return (bus, node, ch);
    }

    /// <summary>Build a $36 sub $00 download payload with a 4-byte absolute address.</summary>
    private static byte[] BuildDownload(uint address, byte[] data)
    {
        var buf = new byte[6 + data.Length];
        buf[0] = 0x36;
        buf[1] = 0x00;
        buf[2] = (byte)((address >> 24) & 0xFF);
        buf[3] = (byte)((address >> 16) & 0xFF);
        buf[4] = (byte)((address >> 8) & 0xFF);
        buf[5] = (byte)(address & 0xFF);
        data.CopyTo(buf, 6);
        return buf;
    }

    [Fact]
    public void Mirror_36_writes_into_matching_erase_region()
    {
        var (_, node, ch) = Wire(captureOn: true);

        // $31 erase 256 bytes at 0x001C0000.
        Service31Handler.Handle(node,
            new byte[] { 0x31, 0x01, 0xFF, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00 },
            ch);
        // Drain $71.
        Assert.True(ch.RxQueue.TryDequeue(out _));

        // $34 RequestDownload sized to cover what the $36 will deliver.
        Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x00, 0x00, 0x01, 0x00 }, ch);
        Assert.True(ch.RxQueue.TryDequeue(out _));

        // $36 writes 16 bytes into the region at offset 0x10.
        var payload = new byte[16];
        for (int i = 0; i < 16; i++) payload[i] = (byte)(0xA0 + i);
        Service36Handler.Handle(node, BuildDownload(0x001C0010, payload), ch);
        Assert.True(ch.RxQueue.TryDequeue(out _));

        var region = Assert.Single(node.State.CapturedFlashRegions);
        Assert.Equal(16u, region.BytesWritten);
        // Bytes before the write stay at $FF.
        for (int i = 0; i < 0x10; i++) Assert.Equal(0xFF, region.Buffer[i]);
        // Mirrored payload at the right offset.
        for (int i = 0; i < 16; i++) Assert.Equal(payload[i], region.Buffer[0x10 + i]);
        // Bytes after the write stay at $FF.
        for (int i = 0x10 + 16; i < 0x100; i++) Assert.Equal(0xFF, region.Buffer[i]);
    }

    [Fact]
    public void Write_outside_region_does_not_mirror()
    {
        var (_, node, ch) = Wire(captureOn: true);

        // Erase region 0x001C0000 + 0x100.
        Service31Handler.Handle(node,
            new byte[] { 0x31, 0x01, 0xFF, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00 },
            ch);
        ch.RxQueue.TryDequeue(out _);

        Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x00, 0x00, 0x10, 0x00 }, ch);
        ch.RxQueue.TryDequeue(out _);

        // Write at an address well outside [0x1C0000, 0x1C0100). The simulator
        // still accepts the $36 (capture mode is permissive); the flash region
        // just doesn't get the data.
        Service36Handler.Handle(node, BuildDownload(0x003FB800, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }), ch);
        ch.RxQueue.TryDequeue(out _);

        var region = Assert.Single(node.State.CapturedFlashRegions);
        Assert.Equal(0u, region.BytesWritten);
        // Whole region stays $FF.
        Assert.All(region.Buffer, b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void Round_trip_source_bin_matches_captured_flash_byte_for_byte()
    {
        // The promise: whatever the kernel actually wrote should land in the
        // captured flash file with no extra, missing, or transposed bytes.
        // We build a full-region source bin (the analogue of the .bin file
        // a host like powerpcm would push), send every byte of it via $36,
        // and assert File.ReadAllBytes(flashFile) == source. The source
        // deliberately includes embedded $FF bytes so a bug that confuses
        // "written" with "still erased" can't masquerade as success.
        var (bus, node, ch) = Wire(captureOn: true);
        var tmp = Path.Combine(Path.GetTempPath(), "GmEcuSimFlashTest_" + Guid.NewGuid().ToString("N"));
        bus.Capture.CaptureDirectory = tmp;
        var written = new List<string>();
        bus.Capture.CaptureWritten += p => written.Add(p);

        // Non-const so the (byte) casts below don't trip CS0221 on compile-time
        // overflow - the runtime truncation is the intended behaviour.
        uint regionAddr = 0x001C0000;
        int regionSize = 256;

        // Pseudo-random pattern with a few intentional $FF bytes embedded -
        // those exercise the path where a SENT byte happens to equal the
        // erase-fill value, which must still round-trip correctly.
        var source = new byte[regionSize];
        for (int i = 0; i < regionSize; i++) source[i] = (byte)((i * 7 + 0x42) & 0xFF);
        source[0]   = 0xFF;
        source[83]  = 0xFF;
        source[200] = 0xFF;
        source[regionSize - 1] = 0xFF;

        try
        {
            // $31 erase covering the whole future write.
            Service31Handler.Handle(node, new byte[]
            {
                0x31, 0x01, 0xFF, 0x00,
                (byte)(regionAddr >> 24), (byte)(regionAddr >> 16),
                (byte)(regionAddr >> 8),  (byte)regionAddr,
                0x00, 0x00, (byte)(regionSize >> 8), (byte)regionSize,
            }, ch);
            ch.RxQueue.TryDequeue(out _);

            // $34 RequestDownload sized to the region.
            Service34Handler.Handle(node, new byte[]
            {
                0x34, 0x00,
                0x00, 0x00, (byte)(regionSize >> 8), (byte)regionSize,
            }, ch);
            ch.RxQueue.TryDequeue(out _);

            // Send the whole source via two $36 halves to exercise the
            // mid-buffer offset path. Together they cover every byte of
            // the erased region.
            int half = regionSize / 2;
            Service36Handler.Handle(node, BuildDownload(regionAddr,        source[..half]), ch);
            ch.RxQueue.TryDequeue(out _);
            Service36Handler.Handle(node, BuildDownload(regionAddr + (uint)half, source[half..]), ch);
            ch.RxQueue.TryDequeue(out _);

            // Session end. EcuExitLogic flushes regions before
            // ClearProgrammingState wipes them.
            EcuExitLogic.Run(node, bus.Scheduler, ch);

            var flashFile = written.SingleOrDefault(p =>
                Path.GetFileName(p).Contains($"flash_{regionAddr:X8}_{regionSize}"));
            Assert.NotNull(flashFile);
            Assert.True(File.Exists(flashFile));

            byte[] captured = File.ReadAllBytes(flashFile!);

            // The core promise: captured contents == source contents, exactly.
            // Same length, same bytes at every offset. Nothing extra, nothing
            // missing.
            Assert.Equal(source.Length, captured.Length);
            Assert.Equal(source, captured);

            // Region list cleared after exit.
            Assert.Empty(node.State.CapturedFlashRegions);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Partial_kernel_write_leaves_unwritten_bytes_as_erased_FF()
    {
        // If the kernel writes only part of the erased region (e.g. a
        // calibration file shorter than the erase block, which real GM
        // tools sometimes do), the unwritten bytes must remain at $FF in
        // the captured image - matching the on-device reality, since real
        // NOR flash holds $FF in cells that weren't programmed after erase.
        // This is the converse of the round-trip test above: it documents
        // what the capture looks like when the source doesn't fill the
        // whole region.
        var (bus, node, ch) = Wire(captureOn: true);
        var tmp = Path.Combine(Path.GetTempPath(), "GmEcuSimFlashTest_" + Guid.NewGuid().ToString("N"));
        bus.Capture.CaptureDirectory = tmp;
        var written = new List<string>();
        bus.Capture.CaptureWritten += p => written.Add(p);

        try
        {
            Service31Handler.Handle(node,
                new byte[] { 0x31, 0x01, 0xFF, 0x00, 0x00, 0x1C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00 },
                ch);
            ch.RxQueue.TryDequeue(out _);

            Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x00, 0x00, 0x01, 0x00 }, ch);
            ch.RxQueue.TryDequeue(out _);

            // Write only 8 bytes at the start. The remaining 248 bytes of
            // the erased region were never sent.
            var partial = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
            Service36Handler.Handle(node, BuildDownload(0x001C0000, partial), ch);
            ch.RxQueue.TryDequeue(out _);

            EcuExitLogic.Run(node, bus.Scheduler, ch);

            var flashFile = written.Single(p =>
                Path.GetFileName(p).Contains("flash_001C0000_256"));
            byte[] captured = File.ReadAllBytes(flashFile);

            // What WAS sent round-trips exactly.
            Assert.Equal(partial, captured.AsSpan(0, partial.Length).ToArray());
            // What WASN'T sent stays at the post-erase $FF.
            for (int i = partial.Length; i < 256; i++)
                Assert.Equal(0xFF, captured[i]);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Session_end_with_no_regions_writes_nothing()
    {
        // The flash-region writer must be inert when the kernel never sent
        // a $31 EraseMemoryByAddress - existing per-$36 capture is the only
        // sink in that case.
        var (bus, node, ch) = Wire(captureOn: true);
        var tmp = Path.Combine(Path.GetTempPath(), "GmEcuSimFlashTest_" + Guid.NewGuid().ToString("N"));
        bus.Capture.CaptureDirectory = tmp;
        var written = new List<string>();
        bus.Capture.CaptureWritten += p => written.Add(p);

        try
        {
            EcuExitLogic.Run(node, bus.Scheduler, ch);
            Assert.DoesNotContain(written, p => Path.GetFileName(p).Contains("flash_"));
            Assert.False(Directory.Exists(tmp)); // no captures dir created when nothing to write
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Capture_off_session_end_writes_no_flash_bin()
    {
        // Belt and braces: even if a region were somehow recorded with
        // capture off, the writer's own gate must skip the write so no
        // file lands on disk.
        var (bus, node, ch) = Wire(captureOn: false);
        var tmp = Path.Combine(Path.GetTempPath(), "GmEcuSimFlashTest_" + Guid.NewGuid().ToString("N"));
        bus.Capture.CaptureDirectory = tmp;
        // Manually inject a region to simulate the impossible path.
        node.State.CapturedFlashRegions.Add(new FlashEraseRegion(0x001C0000, 0x100));

        try
        {
            EcuExitLogic.Run(node, bus.Scheduler, ch);
            Assert.False(Directory.Exists(tmp));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }
}
