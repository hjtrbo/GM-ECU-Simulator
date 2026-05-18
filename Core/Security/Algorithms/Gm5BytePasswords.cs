using System.Reflection;
using System.Security.Cryptography;

namespace Core.Security.Algorithms;

// Per-algorithm key material for the GMW3110 5-byte SecurityAccess
// algorithm, indexed by the algorithm-id byte (0x00..0xFF). Each entry is
// a 62-char ASCII blob consumed by Gm5ByteAlgorithm: a 2-char decimal
// length marker ("01") followed by 60 chars of base64. See Gm5ByteAlgorithm
// for the cipher math; this file holds only data.
//
// Storage form: AES-128-CBC ciphertext of the concatenated 256 x 62-byte
// ASCII payload, embedded as Gm5BytePasswords.bin. The key/IV live in this
// file - this is obfuscation against repo-grep, not real encryption.
internal static class Gm5BytePasswords
{
    private const string ResourceName = "Core.Security.Algorithms.Gm5BytePasswords.bin";
    private const int    EntryCount   = 256;
    private const int    EntryLength  = 62;

    private static readonly byte[] Key = { 0x7D, 0xE2, 0xDB, 0x8C, 0xF1, 0xCA, 0x0D, 0x6B, 0x05, 0x2E, 0x51, 0x38, 0x99, 0x5D, 0x25, 0x51 };
    private static readonly byte[] Iv  = { 0xC1, 0xE0, 0x2C, 0xB7, 0x5E, 0x27, 0xB4, 0x23, 0x3F, 0xAB, 0x7B, 0x79, 0x6B, 0xE6, 0x3E, 0x6B };

    public static readonly IReadOnlyDictionary<int, string> Table = Load();

    private static IReadOnlyDictionary<int, string> Load()
    {
        var asm = typeof(Gm5BytePasswords).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        var ciphertext = new byte[stream.Length];
        stream.ReadExactly(ciphertext);

        using var aes = Aes.Create();
        aes.Key     = Key;
        aes.IV      = Iv;
        aes.Mode    = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        var plaintext = aes.DecryptCbc(ciphertext, Iv);

        if (plaintext.Length != EntryCount * EntryLength)
            throw new InvalidOperationException($"Decrypted blob is {plaintext.Length} bytes, expected {EntryCount * EntryLength}.");

        var table = new Dictionary<int, string>(EntryCount);
        for (int i = 0; i < EntryCount; i++)
            table[i] = System.Text.Encoding.ASCII.GetString(plaintext, i * EntryLength, EntryLength);
        return table;
    }
}
