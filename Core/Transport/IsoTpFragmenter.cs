using Common.PassThru;
using Common.Protocol;
using Core.Bus;

namespace Core.Transport;

// ISO 15765-2 sender. Splits an outbound USDT message into Single Frame (≤7B)
// or First Frame + Consecutive Frame(s) and enqueues each as a PASSTHRU_MSG
// onto the channel's Rx queue.
//
// Step 5 simplification: we don't wait for a Flow Control from the tester.
// The DataLogger and most J2534 hosts auto-handle FC at the driver level;
// for raw CAN at our level the host's J2534 stack ACKs the FF immediately,
// so blasting CFs back-to-back works for typical hosts. Real BS/STmin pacing
// can be added later if a tool requires it.
public static class IsoTpFragmenter
{
    public static void EnqueueResponse(ChannelSession ch, uint canId, ReadOnlySpan<byte> usdtPayload)
    {
        if (usdtPayload.Length <= 7)
        {
            // Single Frame: PCI = 0x0N, length N (1..7)
            var buf = new byte[CanFrame.IdBytes + 1 + usdtPayload.Length];
            CanFrame.WriteId(buf, canId);
            buf[CanFrame.IdBytes] = (byte)(usdtPayload.Length & 0x0F);
            usdtPayload.CopyTo(buf.AsSpan(CanFrame.IdBytes + 1));
            ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = buf });
            return;
        }

        // First Frame: bytes [0..1] = 0x1XYZ where total length is 12-bit (0..4095).
        int total = usdtPayload.Length;
        if (total > 0xFFF) throw new ArgumentException($"USDT payload {total} exceeds 4095-byte ISO-TP limit");

        var ffBuf = new byte[CanFrame.IdBytes + 8];
        CanFrame.WriteId(ffBuf, canId);
        ffBuf[CanFrame.IdBytes + 0] = (byte)((byte)PciType.First | (byte)((total >> 8) & 0x0F));
        ffBuf[CanFrame.IdBytes + 1] = (byte)(total & 0xFF);
        int firstChunk = Math.Min(6, total);
        usdtPayload.Slice(0, firstChunk).CopyTo(ffBuf.AsSpan(CanFrame.IdBytes + 2));
        ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = ffBuf });

        int sent = firstChunk;
        byte seq = 1;
        while (sent < total)
        {
            int chunk = Math.Min(7, total - sent);
            var cfBuf = new byte[CanFrame.IdBytes + 1 + chunk];
            CanFrame.WriteId(cfBuf, canId);
            cfBuf[CanFrame.IdBytes] = (byte)((byte)PciType.Consecutive | (seq & 0x0F));
            usdtPayload.Slice(sent, chunk).CopyTo(cfBuf.AsSpan(CanFrame.IdBytes + 1));
            ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = cfBuf });
            sent += chunk;
            seq++;
        }
    }
}
