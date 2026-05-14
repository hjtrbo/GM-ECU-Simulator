using Common.PassThru;
using Core.Bus;
using Core.Ecu;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// Parallel to ProgrammingSequenceTests, but exercising a CAN-protocol channel
// instead of an ISO15765 channel: the test does the ISO 15765-2 framing
// (SF/FF/CF/FC) by hand, the same way a J2534 host with a hand-rolled TP stack
// would (the GM Security Tester sibling project being the reference).
//
// The simulator-side path is identical for both protocols: bus routes raw CAN
// frames to the ECU's reassembler / fragmenter regardless of channel protocol;
// the only difference is who does the framing on the OTHER side of the J2534
// driver. Locking this in as a test guards against any future change that
// silently degrades the raw-CAN path.
public class ProgrammingSequenceCanProtocolTests
{
    private const ushort PhysReq = NodeFactory.PhysReq;       // $7E0
    private const ushort UsdtResp = NodeFactory.UsdtResp;     // $7E8

    private static (VirtualBus bus, EcuNode node, ChannelSession ch) SetupBusAndChannel()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNodeWithGenericModule();
        bus.AddNode(node);

        var ch = new ChannelSession
        {
            Id = 1,
            Protocol = ProtocolID.CAN,                        // raw CAN, no IsoChannel
            Baud = 500_000,
            Bus = bus,
        };
        return (bus, node, ch);
    }

    // -----------------------------------------------------------------------
    // Wire-format helpers - the test code becomes the host's TP stack
    // -----------------------------------------------------------------------

    private static byte[] WrapCanFrame(uint canId, ReadOnlySpan<byte> data)
    {
        var f = new byte[4 + data.Length];
        f[0] = (byte)((canId >> 24) & 0xFF);
        f[1] = (byte)((canId >> 16) & 0xFF);
        f[2] = (byte)((canId >> 8) & 0xFF);
        f[3] = (byte)(canId & 0xFF);
        data.CopyTo(f.AsSpan(4));
        return f;
    }

    private static (uint canId, byte[] data) UnwrapCanFrame(PassThruMsg msg)
    {
        var raw = msg.Data;
        uint canId = ((uint)raw[0] << 24) | ((uint)raw[1] << 16)
                   | ((uint)raw[2] << 8)  | raw[3];
        return (canId, raw.AsSpan(4).ToArray());
    }

    private static void DispatchHostFrame(VirtualBus bus, ChannelSession ch, uint canId, byte[] data)
        => bus.DispatchHostTx(WrapCanFrame(canId, data), ch);

    private static (uint canId, byte[] data) DequeueResponseFrame(ChannelSession ch)
    {
        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected a response frame on RxQueue");
        return UnwrapCanFrame(msg!);
    }

    /// <summary>
    /// Sends a USDT request to the ECU as raw CAN frames. Handles SF, short FF
    /// + CFs, and escape FF (FF_DL > 4095) + CFs. After an FF, pops the ECU's
    /// FC.CTS from RxQueue and uses its BS / STmin (well, ignores them - we
    /// know the simulator returns BS=0 / STmin=0).
    /// </summary>
    private static void SendUsdtRequest(VirtualBus bus, ChannelSession ch, byte[] payload)
    {
        if (payload.Length <= 7)
        {
            // SingleFrame: PCI 0x0N + N data bytes.
            var sf = new byte[1 + payload.Length];
            sf[0] = (byte)(payload.Length & 0x0F);
            payload.CopyTo(sf, 1);
            DispatchHostFrame(bus, ch, PhysReq, sf);
            return;
        }

        int total = payload.Length;
        byte[] ff;
        int firstChunkBytes;
        if (total <= 4095)
        {
            // Short FF: PCI bytes [0x1H, LL] where 12-bit length spans the low
            // nibble of byte 0 and all of byte 1, then up to 6 data bytes.
            ff = new byte[8];
            ff[0] = (byte)(0x10 | ((total >> 8) & 0x0F));
            ff[1] = (byte)(total & 0xFF);
            firstChunkBytes = Math.Min(6, total);
            payload.AsSpan(0, firstChunkBytes).CopyTo(ff.AsSpan(2));
        }
        else
        {
            // Escape FF (§9.6.3.1): byte 0 = 0x10, byte 1 = 0x00, then 32-bit
            // BE FF_DL in bytes 2..5, then up to 2 data bytes.
            ff = new byte[8];
            ff[0] = 0x10;
            ff[1] = 0x00;
            ff[2] = (byte)((total >> 24) & 0xFF);
            ff[3] = (byte)((total >> 16) & 0xFF);
            ff[4] = (byte)((total >> 8) & 0xFF);
            ff[5] = (byte)(total & 0xFF);
            firstChunkBytes = Math.Min(2, total);
            payload.AsSpan(0, firstChunkBytes).CopyTo(ff.AsSpan(6));
        }
        DispatchHostFrame(bus, ch, PhysReq, ff);

        // ECU's reassembler emitted FC.CTS via the callback - drain it from
        // RxQueue and verify it's well-formed before we keep going.
        var (fcCanId, fcData) = DequeueResponseFrame(ch);
        Assert.Equal(UsdtResp, fcCanId);
        Assert.Equal(0x30, fcData[0] & 0xF0);     // FlowControl PCI
        Assert.Equal(0x00, fcData[0] & 0x0F);     // ContinueToSend
        // fcData[1] = BS, fcData[2] = STmin. The existing reassembler emits
        // 0/0; we don't need to honour them but a real host's stack would.

        // Stream the remaining bytes as ConsecutiveFrames.
        int written = firstChunkBytes;
        byte sn = 1;
        while (written < total)
        {
            int chunk = Math.Min(7, total - written);
            var cf = new byte[1 + chunk];
            cf[0] = (byte)(0x20 | (sn & 0x0F));
            payload.AsSpan(written, chunk).CopyTo(cf.AsSpan(1));
            DispatchHostFrame(bus, ch, PhysReq, cf);
            written += chunk;
            sn = (byte)((sn + 1) & 0x0F);
        }
    }

    /// <summary>
    /// Reassembles a USDT response from the ECU. Pops the first frame; if SF
    /// returns immediately, otherwise issues an FC.CTS (BS=0, STmin=0) back to
    /// the simulator and drains the cascaded CFs.
    /// </summary>
    private static byte[] ReceiveUsdtResponse(VirtualBus bus, ChannelSession ch)
    {
        var (firstCanId, firstData) = DequeueResponseFrame(ch);
        Assert.Equal(UsdtResp, firstCanId);

        byte b0 = firstData[0];
        int pciHigh = b0 & 0xF0;

        if (pciHigh == 0x00)
        {
            // SF: low nibble = SF_DL.
            int sfDl = b0 & 0x0F;
            return firstData.AsSpan(1, sfDl).ToArray();
        }
        if (pciHigh != 0x10)
            throw new Xunit.Sdk.XunitException($"first response frame has unexpected PCI 0x{b0:X2}");

        // FirstFrame - either short or escape encoding.
        int total;
        int firstChunkOffset;
        int totalShort = ((b0 & 0x0F) << 8) | firstData[1];
        if (totalShort != 0)
        {
            total = totalShort;
            firstChunkOffset = 2;
        }
        else
        {
            // Escape FF: 32-bit length in bytes 2..5.
            total = ((int)firstData[2] << 24) | ((int)firstData[3] << 16)
                  | ((int)firstData[4] << 8)  |       firstData[5];
            firstChunkOffset = 6;
        }

        var buf = new byte[total];
        int initial = Math.Min(total, firstData.Length - firstChunkOffset);
        if (initial > 0)
            firstData.AsSpan(firstChunkOffset, initial).CopyTo(buf);
        int written = initial;

        // Send FC.CTS, BS=0, STmin=0. The bus routes this to the fragmenter,
        // which then drains all remaining CFs back-to-back into RxQueue (BS=0
        // means "send everything without further FC"; STmin=0 means no pacing).
        DispatchHostFrame(bus, ch, PhysReq, new byte[] { 0x30, 0x00, 0x00 });

        byte expectedSn = 1;
        while (written < total)
        {
            var (cfCanId, cfData) = DequeueResponseFrame(ch);
            Assert.Equal(UsdtResp, cfCanId);
            Assert.Equal(0x20, cfData[0] & 0xF0);
            Assert.Equal(expectedSn & 0x0F, cfData[0] & 0x0F);
            int chunk = Math.Min(total - written, cfData.Length - 1);
            cfData.AsSpan(1, chunk).CopyTo(buf.AsSpan(written));
            written += chunk;
            expectedSn = (byte)((expectedSn + 1) & 0x0F);
        }
        return buf;
    }

    private static byte[] SendAndReceive(VirtualBus bus, ChannelSession ch, byte[] request)
    {
        SendUsdtRequest(bus, ch, request);
        return ReceiveUsdtResponse(bus, ch);
    }

    private static void SendExpectingNoResponse(VirtualBus bus, ChannelSession ch, byte[] request)
    {
        SendUsdtRequest(bus, ch, request);
        Assert.False(ch.RxQueue.TryDequeue(out _),
            "did not expect a response from the ECU but RxQueue is non-empty");
    }

    // -----------------------------------------------------------------------
    // Full programming sequence over CAN protocol with a 4 KiB synthetic payload
    // -----------------------------------------------------------------------

    [Fact]
    public void Full_programming_sequence_4KB_payload_round_trips_over_raw_can()
    {
        var (bus, node, ch) = SetupBusAndChannel();

        // 1. $10 $02 InitiateDiagnosticOperation.
        Assert.Equal(new byte[] { 0x50, 0x02 }, SendAndReceive(bus, ch, new byte[] { 0x10, 0x02 }));

        // 2. $28 DisableNormalCommunication.
        Assert.Equal(new byte[] { 0x68 }, SendAndReceive(bus, ch, new byte[] { 0x28 }));

        // 3. $A5 $01 requestProgrammingMode.
        Assert.Equal(new byte[] { 0xE5 }, SendAndReceive(bus, ch, new byte[] { 0xA5, 0x01 }));

        // 4. $A5 $03 enableProgrammingMode (no response per §8.17.3 M2).
        SendExpectingNoResponse(bus, ch, new byte[] { 0xA5, 0x03 });
        Assert.True(node.State.ProgrammingModeActive);

        // 5. $27 $01 requestSeed (FakeSeedKeyAlgorithm default seed = $1234).
        Assert.Equal(new byte[] { 0x67, 0x01, 0x12, 0x34 },
            SendAndReceive(bus, ch, new byte[] { 0x27, 0x01 }));

        // 6. $27 $02 sendKey (default expected key = $ABCD).
        Assert.Equal(new byte[] { 0x67, 0x02 },
            SendAndReceive(bus, ch, new byte[] { 0x27, 0x02, 0xAB, 0xCD }));

        // 7. $34 RequestDownload, no compression, 3-byte size = 4096.
        Assert.Equal(new byte[] { 0x74 },
            SendAndReceive(bus, ch, new byte[] { 0x34, 0x00, 0x00, 0x10, 0x00 }));

        // 8. $36 TransferData with 4 KiB - escape FF on the request side
        //    (4101 total bytes including SID/sub/address > 4095).
        var payload = new byte[4096];
        new Random(42).NextBytes(payload);
        var transferRequest = new byte[2 + 3 + payload.Length];
        transferRequest[0] = 0x36;
        transferRequest[1] = 0x00;
        transferRequest[2] = 0x00;
        transferRequest[3] = 0x00;
        transferRequest[4] = 0x00;
        Array.Copy(payload, 0, transferRequest, 5, payload.Length);

        Assert.Equal(new byte[] { 0x76 }, SendAndReceive(bus, ch, transferRequest));

        // 9. Sink buffer must match what we sent.
        Assert.Equal(4096u, node.State.DownloadBytesReceived);
        Assert.Equal(payload, node.State.DownloadBuffer);

        // 10. $20 ReturnToNormalMode ends the session.
        Assert.Equal(new byte[] { 0x60 }, SendAndReceive(bus, ch, new byte[] { 0x20 }));
        Assert.False(node.State.ProgrammingModeActive);
        Assert.False(node.State.DownloadActive);
        Assert.Null(node.State.DownloadBuffer);
    }

    // -----------------------------------------------------------------------
    // Smaller payload to also cover the short-FF (12-bit FF_DL) request path.
    // The 4 KiB test above uses escape FF; this one stays ≤ 4095 for symmetry.
    // -----------------------------------------------------------------------

    [Fact]
    public void Programming_sequence_with_2KB_payload_uses_short_FF_and_round_trips()
    {
        var (bus, node, ch) = SetupBusAndChannel();
        SendAndReceive(bus, ch, new byte[] { 0x28 });
        SendAndReceive(bus, ch, new byte[] { 0xA5, 0x01 });
        SendExpectingNoResponse(bus, ch, new byte[] { 0xA5, 0x03 });
        SendAndReceive(bus, ch, new byte[] { 0x27, 0x01 });
        SendAndReceive(bus, ch, new byte[] { 0x27, 0x02, 0xAB, 0xCD });

        // 2048 bytes -> request total = 2 + 3 + 2048 = 2053 -> short FF (FF_DL = 2053).
        SendAndReceive(bus, ch, new byte[] { 0x34, 0x00, 0x00, 0x08, 0x00 });

        var payload = new byte[2048];
        new Random(7).NextBytes(payload);
        var req = new byte[2 + 3 + payload.Length];
        req[0] = 0x36;
        req[1] = 0x00;
        req[2] = 0x00; req[3] = 0x00; req[4] = 0x00;
        Array.Copy(payload, 0, req, 5, payload.Length);

        Assert.Equal(new byte[] { 0x76 }, SendAndReceive(bus, ch, req));
        Assert.Equal(payload, node.State.DownloadBuffer);
    }

    // -----------------------------------------------------------------------
    // Multi-frame RESPONSE: read a long $22 PID list to exercise the
    // host-as-TP-receiver path (host sends FC.CTS, simulator's fragmenter
    // streams CFs paced by it). For a known long response we use $7F $99 $99
    // synthesised by sending a malformed $36 with a huge address - actually
    // simpler: just use the FakeSeedKeyAlgorithm with a longer seed and
    // verify multi-frame seed responses round-trip.
    // -----------------------------------------------------------------------

    [Fact]
    public void Multi_frame_seed_response_streams_through_test_FC()
    {
        // Build a node with a security algorithm that issues a 50-byte seed,
        // forcing the $27 response to be multi-frame (FF + 7 CFs).
        var bus = new VirtualBus();
        var algo = new FakeSeedKeyAlgorithm
        {
            SeedLength = 50,
            KeyLength = 50,
            SeedToReturn = new byte[50],
            ExpectedKey = new byte[50],
        };
        for (int i = 0; i < 50; i++) algo.SeedToReturn[i] = (byte)(0xA0 + i);
        var node = NodeFactory.CreateNodeWithGenericModule(algo);
        bus.AddNode(node);

        var ch = new ChannelSession
        {
            Id = 1,
            Protocol = ProtocolID.CAN,
            Baud = 500_000,
            Bus = bus,
        };

        // $27 $01 requestSeed - response = [0x67, 0x01, ...50 seed bytes] = 52 bytes -> FF + CFs.
        var resp = SendAndReceive(bus, ch, new byte[] { 0x27, 0x01 });

        Assert.Equal(52, resp.Length);
        Assert.Equal((byte)0x67, resp[0]);
        Assert.Equal((byte)0x01, resp[1]);
        for (int i = 0; i < 50; i++)
            Assert.Equal((byte)(0xA0 + i), resp[2 + i]);
    }
}
