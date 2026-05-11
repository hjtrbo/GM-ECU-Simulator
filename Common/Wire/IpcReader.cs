using System.Buffers.Binary;
using Common.PassThru;

namespace Common.Wire;

// Stream-style binary reader over a byte buffer. All multi-byte values are
// little-endian. Throws InvalidDataException on truncation.
public ref struct IpcReader
{
    private readonly ReadOnlySpan<byte> buf;
    private int pos;

    public IpcReader(ReadOnlySpan<byte> buf) { this.buf = buf; pos = 0; }

    public int Position => pos;
    public int Remaining => buf.Length - pos;

    public byte ReadByte()
    {
        if (Remaining < 1) throw new InvalidDataException("IpcReader: underflow on byte");
        return buf[pos++];
    }

    public ushort ReadU16()
    {
        if (Remaining < 2) throw new InvalidDataException("IpcReader: underflow on u16");
        var v = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(pos, 2));
        pos += 2;
        return v;
    }

    public uint ReadU32()
    {
        if (Remaining < 4) throw new InvalidDataException("IpcReader: underflow on u32");
        var v = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(pos, 4));
        pos += 4;
        return v;
    }

    public byte[] ReadBytes(int count)
    {
        if (count < 0) throw new InvalidDataException($"IpcReader: negative byte count {count}");
        if (Remaining < count) throw new InvalidDataException($"IpcReader: underflow on {count} bytes");
        var v = buf.Slice(pos, count).ToArray();
        pos += count;
        return v;
    }

    public string ReadStringU16Length()
    {
        var len = ReadU16();
        var bytes = ReadBytes(len);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    // 24-byte header + DataSize bytes payload. The full 4128-byte buffer is
    // never on the wire — only the prefix the simulator/shim has bound. We
    // enforce PassThruMsg.MaxDataSize as a hard cap so a malformed
    // dataSize > Int32.MaxValue can't cast to a negative int.
    public PassThruMsg ReadPassThruMsg()
    {
        var msg = new PassThruMsg
        {
            ProtocolID = (ProtocolID)ReadU32(),
            RxStatus = (RxStatus)ReadU32(),
            TxFlags = (TxFlag)ReadU32(),
            Timestamp = ReadU32(),
            ExtraDataIndex = ReadU32(),
        };
        var dataSize = ReadU32();
        if (dataSize > PassThruMsg.MaxDataSize)
            throw new InvalidDataException($"IpcReader: PassThruMsg DataSize {dataSize} exceeds {PassThruMsg.MaxDataSize}");
        msg.Data = ReadBytes((int)dataSize);
        return msg;
    }
}
