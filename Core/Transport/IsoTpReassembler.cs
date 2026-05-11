using Common.Protocol;

namespace Core.Transport;

// ISO 15765-2 receiver. Caller feeds it the data field (post-CAN-ID) of
// each inbound CAN frame; the reassembler returns a complete USDT message
// when one is ready, or null if it's still buffering.
//
// Sends a Flow Control frame back to the tester on First Frame reception
// — that's why it takes a `sendFlowControl` callback. BS=0/STmin=0 only;
// matches the DataLogger's expectation in CanFrameCodec.BuildFlowControl().
public sealed class IsoTpReassembler
{
    private byte[] buffer = [];
    private int totalLen;
    private int written;
    private byte expectedSeq;
    private bool inProgress;

    public delegate void FlowControlEmitter(byte blockSize, byte stMin);

    // Returns the assembled USDT payload (PCI byte stripped from FF; SID + payload only)
    // when complete, otherwise null. Single-frame requests are returned immediately.
    public byte[]? Feed(ReadOnlySpan<byte> data, FlowControlEmitter? emitFc)
    {
        if (data.Length == 0) return null;
        var pci = (PciType)(data[0] & 0xF0);

        switch (pci)
        {
            case PciType.Single:
            {
                int len = data[0] & 0x0F;
                if (len == 0 || len > data.Length - 1) return null;
                return data.Slice(1, len).ToArray();
            }

            case PciType.First:
            {
                if (data.Length < 2) return null;
                int totalHigh = data[0] & 0x0F;
                int totalLow = data[1];
                totalLen = (totalHigh << 8) | totalLow;
                if (totalLen <= 6) return null;     // FF only valid for >6 bytes

                buffer = new byte[totalLen];
                int firstChunk = Math.Min(totalLen, data.Length - 2);
                data.Slice(2, firstChunk).CopyTo(buffer);
                written = firstChunk;
                expectedSeq = 1;
                inProgress = true;

                emitFc?.Invoke(0, 0);             // CTS, BS=0, STmin=0
                return null;
            }

            case PciType.Consecutive:
            {
                if (!inProgress) return null;     // stray CF
                byte seq = (byte)(data[0] & 0x0F);
                if (seq != (expectedSeq & 0x0F)) { Reset(); return null; }
                int chunk = Math.Min(totalLen - written, data.Length - 1);
                data.Slice(1, chunk).CopyTo(buffer.AsSpan(written));
                written += chunk;
                expectedSeq++;
                if (written >= totalLen)
                {
                    var done = buffer;
                    Reset();
                    return done;
                }
                return null;
            }

            case PciType.FlowControl:
                // FC frames going FROM the tester TO us (the ECU) only matter when WE are
                // sending a multi-frame response and waiting for CTS. The fragmenter
                // handles that path; nothing to do here.
                return null;

            default:
                return null;
        }
    }

    private void Reset()
    {
        buffer = [];
        totalLen = 0;
        written = 0;
        expectedSeq = 0;
        inProgress = false;
    }
}
