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
    public void SecondRequest_MidTransfer_ReturnsNrc22()
    {
        // 6Speed.T43 ships two $34/$36 pairs back-to-back. If the tester
        // erroneously re-issued $34 while the first $36 transfer was still
        // mid-flight, the spec says NRC $22. Simulate that by leaving
        // DownloadBytesReceived strictly less than DownloadDeclaredSize.
        var node = CreateUnlockedProgrammingNode();
        var ch = NodeFactory.CreateChannel();

        // First $34 - accepted.
        Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x0C, 0x20 }, ch);
        TestFrame.DequeueSingleFrameUsdt(ch);

        // Simulate mid-transfer: some bytes received but not all.
        node.State.DownloadBytesReceived = 100;

        // Second $34 - should be rejected with NRC $22 because download is
        // still in progress (100 < 0x0C20).
        var ok = Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x04, 0x00 }, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.RequestDownload, Nrc.ConditionsNotCorrectOrSequenceError },
                     TestFrame.DequeueSingleFrameUsdt(ch));
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
