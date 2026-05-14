using Common.Protocol;

namespace Core.Transport;

// ISO 15765-2 receiver. Caller feeds it the data field (post-CAN-ID) of
// each inbound CAN frame; the reassembler returns a complete USDT message
// when one is ready, or null if it's still buffering.
//
// Sends a Flow Control frame back to the tester on First Frame reception
// - that's why it takes a `sendFlowControl` callback. The BS/STmin tail of
// the FC frame is passed in by the caller (per-ECU configurable in EcuNode);
// defaults to 0/0, which matches the DataLogger's expectation in
// CanFrameCodec.BuildFlowControl().
//
// Supports both 12-bit FF_DL (FF_DL <= 4095) and the 32-bit escape FF_DL
// (FF_DL > 4095) per ISO 15765-2:2016 §9.6.3.1, so programming-event
// $36 TransferData requests larger than 4095 bytes round-trip cleanly.
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
    // fcBlockSize / fcSeparationTime are the BS / STmin bytes the FC frame will
    // carry; the caller reads them from per-ECU config.
    public byte[]? Feed(ReadOnlySpan<byte> data, FlowControlEmitter? emitFc,
                        byte fcBlockSize = 0, byte fcSeparationTime = 0)
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
                int totalLenLocal = (totalHigh << 8) | totalLow;
                int firstChunkOffset;

                if (totalLenLocal == 0)
                {
                    // §9.6.3.1 escape FF: low nibble of byte 1 + byte 2 == 0,
                    // 32-bit FF_DL follows in bytes 3..6 (BE). Required for
                    // FF_DL > 4095 (e.g. programming-event $36 payloads).
                    if (data.Length < 6) return null;
                    long longLen = ((long)data[2] << 24) | ((long)data[3] << 16)
                                 | ((long)data[4] << 8)  | data[5];
                    // §9.6.3.2: escape FF with FF_DL <= 4095 is malformed; ignore.
                    if (longLen <= 4095) return null;
                    if (longLen > int.MaxValue) return null;     // we don't allocate >2 GiB
                    totalLenLocal = (int)longLen;
                    firstChunkOffset = 6;
                }
                else
                {
                    if (totalLenLocal <= 6) return null;          // short FF only valid for >6 bytes
                    firstChunkOffset = 2;
                }

                totalLen = totalLenLocal;
                buffer = new byte[totalLen];
                int firstChunk = Math.Min(totalLen, data.Length - firstChunkOffset);
                if (firstChunk > 0)
                    data.Slice(firstChunkOffset, firstChunk).CopyTo(buffer);
                written = firstChunk;
                expectedSeq = 1;
                inProgress = true;

                emitFc?.Invoke(fcBlockSize, fcSeparationTime);   // CTS + caller-supplied BS/STmin
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
