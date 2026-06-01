using Core.Transport;

namespace Shim.Ipc;

// Wire framing for one CAN frame on the raw-CAN TCP link to the gauge
// simulator. Fixed 13 bytes, no length prefix (constant size keeps both ends a
// trivial read-exactly loop):
//
//   byte 0      flags/DLC : bits[3:0] = DLC (0..8 valid data bytes)
//                           bit[7]    = 29-bit ID flag (1 = extended, 0 = 11-bit)
//                           bits[6:4] = reserved (send 0)
//   bytes 1..4  CAN ID, 32-bit big-endian (identical layout to CanFrame)
//   bytes 5..12 8 data bytes; only the first DLC are meaningful, rest padded 0
//
// The big-endian ID matches the internal [4-byte BE id][payload] frame layout
// (see Core/Transport/CanFrame.cs), so conversion is a copy with no endian
// work. The gauge sim mirrors these 13 bytes on its side. ISO-TP lives on both
// ends; the wire carries single CAN frames (SF/FF/CF/FC) only.
public static class RawCanWire
{
    public const int FrameSize = 13;        // 1 flags/DLC + 4 ID + 8 data
    public const int MaxData = 8;           // classical CAN data field
    private const byte ExtendedIdFlag = 0x80;
    private const int DlcMask = 0x0F;

    // Wire (13 bytes) -> internal [4-byte BE id][DLC data bytes], the form
    // VirtualBus.DispatchHostTx expects. A DLC > 8 (malformed) is clamped to 8.
    public static byte[] ToInternal(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < FrameSize)
            throw new ArgumentException($"raw-CAN wire frame must be {FrameSize} bytes", nameof(wire));

        int dlc = wire[0] & DlcMask;
        if (dlc > MaxData) dlc = MaxData;

        var frame = new byte[CanFrame.IdBytes + dlc];
        wire.Slice(1, CanFrame.IdBytes).CopyTo(frame);                          // ID bytes 1..4 -> 0..3
        wire.Slice(1 + CanFrame.IdBytes, dlc).CopyTo(frame.AsSpan(CanFrame.IdBytes));
        return frame;
    }

    // Internal [4-byte BE id][payload] -> wire. Writes exactly FrameSize bytes
    // into <paramref name="wire"/> (the unused data tail is zero-padded). Sets
    // the 29-bit flag when the ID does not fit in 11 bits.
    public static void FromInternal(ReadOnlySpan<byte> frame, Span<byte> wire)
    {
        if (wire.Length < FrameSize)
            throw new ArgumentException($"raw-CAN wire frame must be {FrameSize} bytes", nameof(wire));

        wire.Slice(0, FrameSize).Clear();
        uint canId = CanFrame.ReadId(frame);
        var payload = CanFrame.Payload(frame);
        int dlc = Math.Min(payload.Length, MaxData);

        wire[0] = (byte)(dlc | (canId > 0x7FF ? ExtendedIdFlag : 0));
        frame.Slice(0, CanFrame.IdBytes).CopyTo(wire.Slice(1));                 // ID 0..3 -> bytes 1..4
        payload.Slice(0, dlc).CopyTo(wire.Slice(1 + CanFrame.IdBytes));
    }
}
