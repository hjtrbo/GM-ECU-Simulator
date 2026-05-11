namespace Common.PassThru;

// J2534 PASSTHRU_MSG. Layout matches the C struct verbatim — header is fixed
// 24 bytes, followed by up to 4128 data bytes. We never marshal the full 4128
// across IPC; only the prefix [0..DataSize) is transmitted.
public sealed class PassThruMsg
{
    public const int MaxDataSize = 4128;

    public ProtocolID ProtocolID;
    public RxStatus RxStatus;
    public TxFlag TxFlags;
    public uint Timestamp;          // microseconds since channel open
    public uint ExtraDataIndex;
    public byte[] Data = [];        // Length == DataSize on the wire

    public uint DataSize => (uint)Data.Length;
}
