using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

// Generated utility bytes only: no vehicle image, captured identifier or DPS file.
internal static class SyntheticArchive
{
    public static void Create(string path, byte[]? action = null)
    {
        int count = action is null ? 1 : 2;
        byte[] bytes = new byte[24 + 16 * count];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(14), (ushort)bytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), 3);
        bytes[24] = 1;
        bytes[25] = action is null ? (byte)0xFF : (byte)0x27;
        if (action is not null)
        {
            action.CopyTo(bytes, 26);
            bytes[30] = 0x67;
            bytes[31] = 2;
            bytes[32] = 0xFF;
            bytes[33] = 2;
            bytes[40] = 2;
            bytes[41] = 0xFF;
        }

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var stream = archive.CreateEntry("12345678.bin").Open())
            stream.Write(bytes);
        using var writer = new StreamWriter(
            archive.CreateEntry("synthetic.tbl").Open(), Encoding.ASCII);
        writer.Write("12345678.bin\0Utility File\0");
    }
}
