using System.Buffers.Binary;
using Common.PassThru;

namespace Common.Wire;

// Append-style binary writer that grows a backing buffer as needed.
// All multi-byte values are little-endian.
public sealed class IpcWriter
{
    private byte[] buf;
    private int pos;

    public IpcWriter(int initialCapacity = 256)
    {
        buf = new byte[Math.Max(64, initialCapacity)];
        pos = 0;
    }

    public int Length => pos;
    public ReadOnlySpan<byte> AsSpan() => buf.AsSpan(0, pos);
    public byte[] ToArray() => buf.AsSpan(0, pos).ToArray();

    private void EnsureCapacity(int extra)
    {
        if (pos + extra <= buf.Length) return;
        int newSize = Math.Max(buf.Length * 2, pos + extra);
        Array.Resize(ref buf, newSize);
    }

    public void WriteByte(byte v)
    {
        EnsureCapacity(1);
        buf[pos++] = v;
    }

    public void WriteU16(ushort v)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos, 2), v);
        pos += 2;
    }

    public void WriteU32(uint v)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos, 4), v);
        pos += 4;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(buf.AsSpan(pos));
        pos += bytes.Length;
    }

    public void WriteStringU16Length(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        if (bytes.Length > ushort.MaxValue)
            throw new ArgumentException("String too long for u16-length encoding");
        WriteU16((ushort)bytes.Length);
        WriteBytes(bytes);
    }

    public void WritePassThruMsg(PassThruMsg msg)
    {
        WriteU32((uint)msg.ProtocolID);
        WriteU32((uint)msg.RxStatus);
        WriteU32((uint)msg.TxFlags);
        WriteU32(msg.Timestamp);
        WriteU32(msg.ExtraDataIndex);
        WriteU32((uint)msg.Data.Length);
        WriteBytes(msg.Data);
    }
}
