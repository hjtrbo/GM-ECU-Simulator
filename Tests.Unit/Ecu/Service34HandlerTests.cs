using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// GMW3110-2010 §8.12 RequestDownload ($34) handler-level tests. The full
// programming sequence (FF/CF, $36, capture mode) is exercised in
// ProgrammingSequenceTests; this file focuses on $34's precondition logic.
public sealed class Service34HandlerTests
{
    private static EcuNode CreateUnlockedProgrammingNode()
    {
        var node = NodeFactory.CreateNode();
        node.State.NormalCommunicationDisabled = true;
        node.State.ProgrammingModeRequested = true;
        node.State.ProgrammingModeActive = true;
        node.State.SecurityUnlockedLevel = 1;
        return node;
    }

    [Fact]
    public void FirstRequest_AcceptedAndAllocatesBuffer()
    {
        var node = CreateUnlockedProgrammingNode();
        var ch = NodeFactory.CreateChannel();

        var ok = Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x0C, 0x20 }, ch);

        Assert.True(ok);
        Assert.True(node.State.DownloadActive);
        Assert.Equal(0x0C20u, node.State.DownloadDeclaredSize);
        Assert.NotNull(node.State.DownloadBuffer);
        Assert.Equal(0x0C20, node.State.DownloadBuffer!.Length);
    }

    [Fact]
    public void SecondRequest_DeclaredLargerThanActualBytes_Accepted()
    {
        // Real DPS programming sessions issue back-to-back $34/$36 pairs where
        // each $34 declares `dataBytesPerMessage` (often $0FFE = 4094) as a
        // generic *buffer-size hint*, then $36 sends the cal file's actual
        // payload which is usually much smaller (cal files run 100 B .. ~200
        // KB). The host then fires the next $34 without sending a $37 Transfer
        // Exit - it knows the current cal is done because it has its file in
        // hand. The simulator must accept the next $34 in that state; the
        // buffer realloc on the new $34 naturally drops the prior partial
        // state.
        //
        // Earlier strict-spec interpretation rejected this with NRC $22 and
        // broke the E38 archive run after exactly 4 cal downloads.
        var node = CreateUnlockedProgrammingNode();
        var ch = NodeFactory.CreateChannel();

        // First $34 - declares 0x0FFE = 4094 bytes (the typical DPS hint).
        Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x0F, 0xFE }, ch);
        TestFrame.DequeueSingleFrameUsdt(ch);

        // Simulate a real cal that delivered way fewer bytes than declared:
        // the 816-byte 12620806.bin we saw on the E38 wire.
        node.State.DownloadBytesReceived = 816;

        // Second $34 - must be accepted (next cal incoming).
        var ok = Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x0F, 0xFE }, ch);

        Assert.True(ok);
        Assert.Equal(new byte[] { Service.Positive(Service.RequestDownload) },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        // Buffer is reallocated at the new declared size; prior received-bytes
        // count is reset to zero so $36 starts a fresh tally for the new cal.
        Assert.Equal(0x0FFEu, node.State.DownloadDeclaredSize);
        Assert.Equal(0u, node.State.DownloadBytesReceived);
        Assert.NotNull(node.State.DownloadBuffer);
        Assert.Equal(0x0FFE, node.State.DownloadBuffer!.Length);
    }

    [Fact]
    public void DpsRealWorldFlow_FiveConsecutive34s_AllAccepted()
    {
        // E38 archive trace: $34 declares $0FFE every time, $36 sends cal
        // file of actual size < $0FFE, repeat for each cal. Five iterations
        // walks past the 4-cal point where the old logic broke.
        var node = CreateUnlockedProgrammingNode();
        var ch = NodeFactory.CreateChannel();

        var actualCalSizes = new uint[] { 1_771_520, 38_620, 8_896, 816, 5_120 };
        foreach (var calSize in actualCalSizes)
        {
            var ok = Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x0F, 0xFE }, ch);
            Assert.True(ok);
            Assert.Equal(new byte[] { Service.Positive(Service.RequestDownload) },
                         TestFrame.DequeueSingleFrameUsdt(ch));
            // Pretend $36 delivered the cal bytes for this section. For sizes
            // larger than 0x0FFE the simulator's spec-mode buffer would have
            // overflowed (cap), but capture-mode handles that elsewhere - the
            // $34 acceptance gate is what we care about here, and the next
            // $34 must clear it regardless of how much landed.
            node.State.DownloadBytesReceived = Math.Min(calSize, node.State.DownloadDeclaredSize);
        }
    }

    [Fact]
    public void SecondRequest_AfterFirstCompletes_Accepted()
    {
        // The 6Speed.T43 happy path: first $34 declares 3104 bytes, $36
        // delivers all 3104, then a second $34 declares the next section.
        // The sim must accept the second $34 - "download in progress" only
        // applies while bytes are still being transferred.
        var node = CreateUnlockedProgrammingNode();
        var ch = NodeFactory.CreateChannel();

        // First $34: declared 0x0C20 = 3104 bytes.
        Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x0C, 0x20 }, ch);
        TestFrame.DequeueSingleFrameUsdt(ch);

        // Simulate completion of the first $36 transfer.
        node.State.DownloadBytesReceived = 0x0C20;

        // Second $34: declared 0x0400 = 1024 bytes (second kernel section).
        var ok = Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x04, 0x00 }, ch);

        Assert.True(ok);
        Assert.Equal(new byte[] { Service.Positive(Service.RequestDownload) },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.True(node.State.DownloadActive);
        Assert.Equal(0x0400u, node.State.DownloadDeclaredSize);
        Assert.Equal(0x0400, node.State.DownloadBuffer!.Length);
        Assert.Equal(0u, node.State.DownloadBytesReceived);
        Assert.Null(node.State.DownloadCaptureBaseAddress);
    }

    [Fact]
    public void SecondRequest_AfterCaptureOverflowed_Accepted()
    {
        // Capture mode allows DownloadBytesReceived to EXCEED the declared
        // size (real GM hosts send absolute addresses outside the declared
        // range). A second $34 in that state should still be accepted.
        var node = CreateUnlockedProgrammingNode();
        var ch = NodeFactory.CreateChannel();

        Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x0C, 0x20 }, ch);
        TestFrame.DequeueSingleFrameUsdt(ch);

        // Capture mode: bytes received far exceed declared size.
        node.State.DownloadBytesReceived = 0x10_0000;

        var ok = Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x04, 0x00 }, ch);

        Assert.True(ok);
        Assert.Equal(new byte[] { Service.Positive(Service.RequestDownload) },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }
}
