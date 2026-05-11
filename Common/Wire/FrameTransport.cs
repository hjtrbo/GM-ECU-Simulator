using System.Buffers.Binary;

namespace Common.Wire;

// Length-prefixed frame transport over an arbitrary Stream (named pipe in our
// case). Each frame: [u32 length][u8 messageType][payload]. The length covers
// type byte + payload, NOT the length field itself.
public static class FrameTransport
{
    private const int MaxFrameSize = 1 << 22;  // 4 MiB sanity cap

    public static async Task WriteFrameAsync(
        Stream stream, byte messageType, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var header = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), (uint)(payload.Length + 1));
        header[4] = messageType;
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<(byte messageType, byte[] payload)> ReadFrameAsync(
        Stream stream, CancellationToken ct)
    {
        var header = new byte[5];
        await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false);
        var len = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        if (len < 1 || len > MaxFrameSize)
            throw new InvalidDataException($"FrameTransport: invalid length {len}");
        var messageType = header[4];
        var payloadLen = (int)(len - 1);
        var payload = new byte[payloadLen];
        if (payloadLen > 0)
            await ReadExactlyAsync(stream, payload, ct).ConfigureAwait(false);
        return (messageType, payload);
    }

    private static async Task ReadExactlyAsync(Stream s, Memory<byte> buf, CancellationToken ct)
    {
        int pos = 0;
        while (pos < buf.Length)
        {
            int n = await s.ReadAsync(buf[pos..], ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("FrameTransport: pipe closed mid-frame");
            pos += n;
        }
    }
}
