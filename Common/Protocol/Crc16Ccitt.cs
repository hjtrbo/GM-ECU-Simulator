namespace Common.Protocol;

// CRC-16/CCITT-FALSE (poly=$1021, init=$FFFF, refIn=false, refOut=false,
// xorOut=$0000). Used by GM SPS programming kernels to answer
// $31 $01 $0401 CheckMemoryByAddress after a calibration download: the
// kernel returns [$71, $04, crc_hi, crc_lo] where crc is computed over
// the bytes the tester wrote via $36 TransferData. powerpcm_flasher's
// inline check uses the table-driven left-shift form
//   crc = Table[((crc >> 8) ^ b) & 0xFF] ^ (crc << 8)
// with Table[i] = CRC-of-byte(i) starting from $0000. This implementation
// uses the bit-shifting form for clarity; the result is bit-identical.
//
// Algorithm reversed from PowerPCM Flasher 0.0.0.6 (Daniel2345 patch),
// Hauptfenster.cs:1697-1750. Verified against pycrc CCITT-FALSE.
public static class Crc16Ccitt
{
    public const ushort InitialValue = 0xFFFF;
    public const ushort Polynomial   = 0x1021;

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = InitialValue;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int b = 0; b < 8; b++)
            {
                if ((crc & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ Polynomial);
                else
                    crc <<= 1;
            }
        }
        return crc;
    }
}
