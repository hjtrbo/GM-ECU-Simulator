namespace Common.Protocol;

// PID engineering data types — controls how raw bytes are interpreted/encoded
// when the simulator returns a sample (big-endian on the wire per GMLAN).
public enum PidDataType
{
    Bool,
    Unsigned,
    Signed,
    Hex,
    Ascii,
}

public enum PidSize : byte
{
    Byte = 1,
    Word = 2,
    DWord = 4,
}
