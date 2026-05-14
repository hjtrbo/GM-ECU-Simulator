using Common.IsoTp;
using Common.PassThru;
using Core.Bus;
using Core.Ecu;
using EcuSimulator.Tests.TestHelpers;
using Shim.IsoTp;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// End-to-end programming sequence per GMW3110-2010 Chapter 9 / §8.9 / §8.17 /
// §8.12 / §8.13. Drives the simulator over the same Iso15765Channel surface
// the J2534 host uses (no native shim hop), with synthetic download payloads.
//
// What this proves:
//   - All four programming services ($28 / $A5 / $34 / $36) co-operate
//   - The FC-aware fragmenter and Iso15765Channel correctly carry multi-frame
//     responses AND multi-frame requests through the in-process bus cascade
//   - Programming-session preconditions enforce in the right order
//   - $20 ReturnToNormalMode wipes the session cleanly
public class ProgrammingSequenceTests
{
    private const ushort PhysReq = NodeFactory.PhysReq;       // $7E0
    private const ushort UsdtResp = NodeFactory.UsdtResp;     // $7E8

    /// <summary>
    /// Wires up a full mini-bus for the programming sequence: VirtualBus +
    /// EcuNode (with security) + ChannelSession with an Iso15765Channel
    /// attached. Mirrors what RequestDispatcher.Connect does for an ISO15765
    /// channel - we just bypass the IPC hop.
    /// </summary>
    private static (VirtualBus bus, EcuNode node, ChannelSession ch, Iso15765Channel iso, FakeSeedKeyAlgorithm algo)
        SetupBusAndChannel()
    {
        var bus = new VirtualBus();
        var algo = new FakeSeedKeyAlgorithm();
        var node = NodeFactory.CreateNodeWithGenericModule(algo);
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

        // FlowControl filter: pattern $7E8 matches the ECU's USDT response,
        // FlowCtl $7E0 is the request CAN ID we send to.
        iso.AddFilter(new Iso15765Channel.IsoFilter
        {
            Id = 1,
            MaskCanId = 0xFFFFFFFF,
            PatternCanId = UsdtResp,
            FlowCtlCanId = PhysReq,
            Format = AddressFormat.Normal,
        });

        return (bus, node, ch, iso, algo);
    }

    /// <summary>
    /// Sends a UDS request and returns the reassembled USDT response payload.
    /// In our synchronous in-process cascade, by the time BeginTransmit's
    /// dispatch returns, the ECU has processed the request, generated the
    /// response, and the response has landed on the IsoChannel queue.
    /// </summary>
    private static byte[] SendAndReceive(Iso15765Channel iso, byte[] request)
    {
        var begin = iso.BeginTransmit(PhysReq, request);
        Assert.True(begin.Started, "BeginTransmit failed - no FlowControl filter?");

        // The first frame leaves through the bus; the cascade drives any
        // remaining CFs synchronously on this thread, ending with the ECU's
        // response landing in iso.ReassembledPayloadQueue.
        iso.BusEgress!(begin.CanFrame!);
        iso.EndTransmit(begin.Filter!);

        Assert.True(iso.ReassembledPayloadQueue.TryDequeue(out var msg),
            $"no response from ECU for request SID 0x{request[0]:X2}");

        // PassThruMsg.Data on an ISO15765 channel = [4 BE CAN_ID][user payload].
        return msg!.Data.AsSpan(4).ToArray();
    }

    /// <summary>
    /// Sends a UDS request that is spec-defined to elicit no response (e.g.
    /// $A5 $03 enableProgrammingMode per §8.17.3 footnote M2). Asserts the
    /// queue stays empty after the cascade completes.
    /// </summary>
    private static void SendExpectingNoResponse(Iso15765Channel iso, byte[] request)
    {
        var begin = iso.BeginTransmit(PhysReq, request);
        Assert.True(begin.Started);
        iso.BusEgress!(begin.CanFrame!);
        iso.EndTransmit(begin.Filter!);
        Assert.False(iso.ReassembledPayloadQueue.TryDequeue(out _),
            "did not expect a response, but ECU enqueued one");
    }

    // -----------------------------------------------------------------------
    // Full programming sequence with a 4 KiB synthetic payload
    // -----------------------------------------------------------------------

    [Fact]
    public void Full_programming_sequence_4KB_payload_round_trips()
    {
        var (_, node, _, iso, _) = SetupBusAndChannel();

        // 1. $10 $02 - initiate diagnostic operation (disableAllDtcs).
        Assert.Equal(new byte[] { 0x50, 0x02 }, SendAndReceive(iso, new byte[] { 0x10, 0x02 }));

        // 2. $28 - disable normal communication.
        Assert.Equal(new byte[] { 0x68 }, SendAndReceive(iso, new byte[] { 0x28 }));
        Assert.True(node.State.NormalCommunicationDisabled);

        // 3. $A5 $01 - requestProgrammingMode (verify, normal speed).
        Assert.Equal(new byte[] { 0xE5 }, SendAndReceive(iso, new byte[] { 0xA5, 0x01 }));
        Assert.True(node.State.ProgrammingModeRequested);

        // 4. $A5 $03 - enableProgrammingMode. §8.17.3 M2: NO response sent.
        SendExpectingNoResponse(iso, new byte[] { 0xA5, 0x03 });
        Assert.True(node.State.ProgrammingModeActive);

        // 5. $27 $01 - requestSeed level 1. FakeSeedKeyAlgorithm default seed = $1234.
        Assert.Equal(new byte[] { 0x67, 0x01, 0x12, 0x34 }, SendAndReceive(iso, new byte[] { 0x27, 0x01 }));

        // 6. $27 $02 - sendKey. FakeSeedKeyAlgorithm default expected key = $ABCD.
        Assert.Equal(new byte[] { 0x67, 0x02 }, SendAndReceive(iso, new byte[] { 0x27, 0x02, 0xAB, 0xCD }));
        Assert.Equal((byte)1, node.State.SecurityUnlockedLevel);

        // 7. $34 RequestDownload, dataFormatIdentifier = $00 (no compression / no
        //    encryption), unCompressedMemorySize = $00 $10 $00 (3-byte BE = 4096).
        Assert.Equal(new byte[] { 0x74 }, SendAndReceive(iso, new byte[] { 0x34, 0x00, 0x00, 0x10, 0x00 }));
        Assert.True(node.State.DownloadActive);
        Assert.Equal(4096u, node.State.DownloadDeclaredSize);

        // 8. $36 TransferData - one multi-frame request carrying the full 4 KiB.
        //    Layout: $36 + sub $00 + 3-byte startingAddress $00 $00 $00 + dataRecord.
        var payload = new byte[4096];
        new Random(42).NextBytes(payload);
        var transferRequest = new byte[2 + 3 + payload.Length];
        transferRequest[0] = 0x36;
        transferRequest[1] = 0x00;       // sub-function = Download
        // startingAddress 0x000000 (offset 0 into the sink buffer)
        transferRequest[2] = 0x00;
        transferRequest[3] = 0x00;
        transferRequest[4] = 0x00;
        Array.Copy(payload, 0, transferRequest, 5, payload.Length);

        Assert.Equal(new byte[] { 0x76 }, SendAndReceive(iso, transferRequest));

        // 9. The simulator's sink buffer must be byte-for-byte what the tester sent.
        Assert.Equal(4096u, node.State.DownloadBytesReceived);
        Assert.NotNull(node.State.DownloadBuffer);
        Assert.Equal(payload, node.State.DownloadBuffer);

        // 10. $20 ReturnToNormalMode - end programming session.
        Assert.Equal(new byte[] { 0x60 }, SendAndReceive(iso, new byte[] { 0x20 }));

        // 11. EcuExitLogic should have wiped the programming/download state.
        //     SecurityUnlockedLevel is intentionally NOT reset - GMW3110
        //     §8.5.6.2 Exit_Diagnostic_Services does not list security re-lock,
        //     so unlock state survives until a power cycle.
        Assert.False(node.State.NormalCommunicationDisabled);
        Assert.False(node.State.ProgrammingModeRequested);
        Assert.False(node.State.ProgrammingModeActive);
        Assert.False(node.State.DownloadActive);
        Assert.Null(node.State.DownloadBuffer);
        Assert.Equal(0u, node.State.DownloadBytesReceived);
    }

    // -----------------------------------------------------------------------
    // Negative-path checks: each precondition gate fires the right NRC
    // -----------------------------------------------------------------------

    [Fact]
    public void A5_03_without_prior_A5_01_returns_NRC_22()
    {
        // §8.17.4 NRC $22: enableProgrammingMode requested without a prior
        // requestProgrammingMode.
        var (_, _, _, iso, _) = SetupBusAndChannel();
        SendAndReceive(iso, new byte[] { 0x28 });

        Assert.Equal(new byte[] { 0x7F, 0xA5, 0x22 }, SendAndReceive(iso, new byte[] { 0xA5, 0x03 }));
    }

    [Fact]
    public void A5_01_without_28_active_returns_NRC_22()
    {
        // §8.17.4 NRC $22: $28 not active.
        var (_, _, _, iso, _) = SetupBusAndChannel();

        Assert.Equal(new byte[] { 0x7F, 0xA5, 0x22 }, SendAndReceive(iso, new byte[] { 0xA5, 0x01 }));
    }

    [Fact]
    public void Service34_without_security_unlock_returns_NRC_22()
    {
        // §8.12.4 NRC $22: security not unlocked.
        var (_, _, _, iso, _) = SetupBusAndChannel();
        SendAndReceive(iso, new byte[] { 0x28 });
        SendAndReceive(iso, new byte[] { 0xA5, 0x01 });
        SendExpectingNoResponse(iso, new byte[] { 0xA5, 0x03 });

        // Skip $27 - $34 should be rejected.
        Assert.Equal(new byte[] { 0x7F, 0x34, 0x22 },
            SendAndReceive(iso, new byte[] { 0x34, 0x00, 0x00, 0x10, 0x00 }));
    }

    [Fact]
    public void Service34_without_programming_mode_returns_NRC_22()
    {
        // §8.12.4 NRC $22: $A5 not active.
        var (_, _, _, iso, _) = SetupBusAndChannel();
        SendAndReceive(iso, new byte[] { 0x28 });
        // Note: skipping $A5; security alone is not enough.
        SendAndReceive(iso, new byte[] { 0x27, 0x01 });
        SendAndReceive(iso, new byte[] { 0x27, 0x02, 0xAB, 0xCD });

        Assert.Equal(new byte[] { 0x7F, 0x34, 0x22 },
            SendAndReceive(iso, new byte[] { 0x34, 0x00, 0x00, 0x10, 0x00 }));
    }

    [Fact]
    public void Service36_without_prior_34_returns_NRC_22()
    {
        // §8.13.4 NRC $22: TransferData_Allowed = NO.
        var (_, _, _, iso, _) = SetupBusAndChannel();
        SendAndReceive(iso, new byte[] { 0x28 });
        SendAndReceive(iso, new byte[] { 0xA5, 0x01 });
        SendExpectingNoResponse(iso, new byte[] { 0xA5, 0x03 });
        SendAndReceive(iso, new byte[] { 0x27, 0x01 });
        SendAndReceive(iso, new byte[] { 0x27, 0x02, 0xAB, 0xCD });

        // Skip $34 - $36 should be rejected.
        Assert.Equal(new byte[] { 0x7F, 0x36, 0x22 },
            SendAndReceive(iso, new byte[] { 0x36, 0x00, 0x00, 0x00, 0x00, 0xAA }));
    }

    [Fact]
    public void Service36_with_address_past_buffer_end_returns_NRC_31()
    {
        // §8.13.4 NRC $31 ROOR: startingAddress + dataRecord exceeds buffer.
        var (_, node, _, iso, _) = SetupBusAndChannel();
        SendAndReceive(iso, new byte[] { 0x28 });
        SendAndReceive(iso, new byte[] { 0xA5, 0x01 });
        SendExpectingNoResponse(iso, new byte[] { 0xA5, 0x03 });
        SendAndReceive(iso, new byte[] { 0x27, 0x01 });
        SendAndReceive(iso, new byte[] { 0x27, 0x02, 0xAB, 0xCD });
        SendAndReceive(iso, new byte[] { 0x34, 0x00, 0x00, 0x00, 0x10 });   // 16-byte buffer

        // Try to write 8 bytes starting at offset 12 - that's bytes 12..19 in a
        // 16-byte buffer, so out-of-range.
        var req = new byte[] { 0x36, 0x00, 0x00, 0x00, 0x0C,
                               0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22 };
        Assert.Equal(new byte[] { 0x7F, 0x36, 0x31 }, SendAndReceive(iso, req));

        // The buffer must be untouched - the rejected $36 doesn't write partials.
        Assert.NotNull(node.State.DownloadBuffer);
        Assert.All(node.State.DownloadBuffer, b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public void Service28_with_extra_bytes_returns_NRC_12()
    {
        // §8.9.4 NRC $12: length != 1.
        var (_, _, _, iso, _) = SetupBusAndChannel();
        Assert.Equal(new byte[] { 0x7F, 0x28, 0x12 }, SendAndReceive(iso, new byte[] { 0x28, 0xAA }));
    }

    [Fact]
    public void Service34_with_unsupported_dataFormatIdentifier_returns_NRC_12()
    {
        // §8.12.4 NRC $12: dataFormatIdentifier value not supported.
        var (_, _, _, iso, _) = SetupBusAndChannel();
        SendAndReceive(iso, new byte[] { 0x28 });
        SendAndReceive(iso, new byte[] { 0xA5, 0x01 });
        SendExpectingNoResponse(iso, new byte[] { 0xA5, 0x03 });
        SendAndReceive(iso, new byte[] { 0x27, 0x01 });
        SendAndReceive(iso, new byte[] { 0x27, 0x02, 0xAB, 0xCD });

        // dataFormatIdentifier = $11 (compression+encryption); not supported by us.
        Assert.Equal(new byte[] { 0x7F, 0x34, 0x12 },
            SendAndReceive(iso, new byte[] { 0x34, 0x11, 0x00, 0x10, 0x00 }));
    }

    // -----------------------------------------------------------------------
    // Sink-buffer behaviour: $36s at different startingAddresses fill in
    // different sections of the buffer.
    // -----------------------------------------------------------------------

    [Fact]
    public void Multiple_36_at_different_addresses_assemble_into_one_buffer()
    {
        var (_, node, _, iso, _) = SetupBusAndChannel();
        SendAndReceive(iso, new byte[] { 0x28 });
        SendAndReceive(iso, new byte[] { 0xA5, 0x01 });
        SendExpectingNoResponse(iso, new byte[] { 0xA5, 0x03 });
        SendAndReceive(iso, new byte[] { 0x27, 0x01 });
        SendAndReceive(iso, new byte[] { 0x27, 0x02, 0xAB, 0xCD });
        SendAndReceive(iso, new byte[] { 0x34, 0x00, 0x00, 0x00, 0x20 });   // 32-byte buffer

        // Write 8 bytes at offset 0
        SendAndReceive(iso, new byte[] { 0x36, 0x00, 0x00, 0x00, 0x00,
                                         0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17 });
        // Write 8 bytes at offset 16
        SendAndReceive(iso, new byte[] { 0x36, 0x00, 0x00, 0x00, 0x10,
                                         0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27 });

        Assert.NotNull(node.State.DownloadBuffer);
        Assert.Equal(0x10, node.State.DownloadBuffer[0]);
        Assert.Equal(0x17, node.State.DownloadBuffer[7]);
        Assert.Equal(0x00, node.State.DownloadBuffer[8]);     // gap stays zero
        Assert.Equal(0x00, node.State.DownloadBuffer[15]);
        Assert.Equal(0x20, node.State.DownloadBuffer[16]);
        Assert.Equal(0x27, node.State.DownloadBuffer[23]);
    }
}
