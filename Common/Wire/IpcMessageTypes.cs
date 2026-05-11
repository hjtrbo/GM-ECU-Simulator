namespace Common.Wire;

// Wire frame: [u32 length][u8 messageType][payload]. Length covers everything
// after the length field itself (i.e., type byte + payload), little-endian.
public static class IpcMessageTypes
{
    public const byte OpenRequest = 0x01;
    public const byte CloseRequest = 0x02;
    public const byte ConnectRequest = 0x03;
    public const byte DisconnectRequest = 0x04;
    public const byte ReadMsgsRequest = 0x05;
    public const byte WriteMsgsRequest = 0x06;
    public const byte StartFilterRequest = 0x07;
    public const byte StopFilterRequest = 0x08;
    public const byte StartPeriodicRequest = 0x09;
    public const byte StopPeriodicRequest = 0x0A;
    public const byte IoctlRequest = 0x0B;
    public const byte ReadVersionRequest = 0x0C;
    public const byte SetVoltageRequest = 0x0D;
    public const byte GetLastErrorRequest = 0x0E;
    public const byte CanaryRequest = 0x0F;     // reserved: round-trip handshake (currently unused by the shim)

    // Responses are request | 0x80
    public const byte OpenResponse = 0x81;
    public const byte CloseResponse = 0x82;
    public const byte ConnectResponse = 0x83;
    public const byte DisconnectResponse = 0x84;
    public const byte ReadMsgsResponse = 0x85;
    public const byte WriteMsgsResponse = 0x86;
    public const byte StartFilterResponse = 0x87;
    public const byte StopFilterResponse = 0x88;
    public const byte StartPeriodicResponse = 0x89;
    public const byte StopPeriodicResponse = 0x8A;
    public const byte IoctlResponse = 0x8B;
    public const byte ReadVersionResponse = 0x8C;
    public const byte SetVoltageResponse = 0x8D;
    public const byte GetLastErrorResponse = 0x8E;
    public const byte CanaryResponse = 0x8F;
}
