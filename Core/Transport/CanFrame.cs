namespace Core.Transport;

// CAN frame layout used by J2534 PASSTHRU_MSG.Data on Protocol.CAN:
// bytes [0..3] = CAN ID (32-bit big-endian; 11-bit IDs fit in the low 11 bits),
// bytes [4..]  = data field (up to 8 bytes for classical CAN).
//
// This matches the convention in
// `Gm Data Logger_v5_Wpf_WIP/Core/LoggerCore/CanFrameCodec.cs:11`.
public static class CanFrame
{
    public const int IdBytes = 4;

    public static uint ReadId(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < IdBytes) throw new ArgumentException("frame too short for CAN ID");
        return ((uint)frame[0] << 24) | ((uint)frame[1] << 16) | ((uint)frame[2] << 8) | frame[3];
    }

    public static void WriteId(Span<byte> frame, uint canId)
    {
        if (frame.Length < IdBytes) throw new ArgumentException("frame too short for CAN ID");
        frame[0] = (byte)((canId >> 24) & 0xFF);
        frame[1] = (byte)((canId >> 16) & 0xFF);
        frame[2] = (byte)((canId >> 8) & 0xFF);
        frame[3] = (byte)(canId & 0xFF);
    }

    public static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> frame)
        => frame.Length > IdBytes ? frame.Slice(IdBytes) : default;
}
