using System.Globalization;
using System.Text.Json;

namespace Core.Security.Algorithms;

// GM T43 / 6T70-family transmission-controller seed-key algorithm. Two-byte
// seed, two-byte key, level 1 only.
//
// Algorithm source: decompiled from the FOSS-licensed 6Speed.T43 tester
// (Form1.cs / Program.cs `gett43key`). That tool is battle-tested against
// real T43 hardware, so this implementation should match a production ECU
// step-for-step.
//
// Programming-session policy: BypassAll. Real T43 boot block (file offset
// 0x2BBFC in a 24264923 image) is a permissive $27 stub - it returns seed
// = 00 00 unconditionally and accepts any key, including the literal 00 00
// that 6Speed.T43 hardcodes at Program.cs:1122. Once Service10Handler sees
// $10 $02 and flips ProgrammingModeActive, the generic module short-circuits
// the algorithm to match. Pre-programming-session $27 (operational mode)
// still uses gett43key.
//
// Reference C# code (gett43key, taking a 4-hex-char seed string):
//     int n = Convert.ToInt32(seedHex, 16);
//     n  = (n + 0xB0D8) & 0xFFFF;
//     n  = ((~n) + 1) & 0xFFFF;        // two's-complement negate
//     n  = (n << 3) | (n >> 13);       // rotate-left-3
//     return byteSwap16(n).ToString("x4");
//
// Verified test vectors (computed from the algorithm above; arithmetic
// is unit-tested in T43AlgorithmTests):
//     0x0000 -> 0x4279
//     0x1234 -> 0xA1E7
//     0xDEAD -> 0xDB83
//     0xCAFE -> 0x5421
//     0xFFFF -> 0x4A79
// Real captured pairs from hardware should be added there as additional
// Theory inputs when available.
//
// Configuration (optional, via SecurityModuleConfig JsonElement):
//     { "fixedSeed": "1234" }   // hex, 4 chars - locks the seed for repeatable testing
// No fixedSeed -> Random.Shared.NextBytes per request (more realistic).
public sealed class T43Algorithm : ISeedKeyAlgorithm
{
    private byte[]? fixedSeed;

    public string Id => "gm-t43";
    public int SeedLength => 2;
    public int KeyLength => 2;
    public IEnumerable<byte> SupportedLevels { get; } = new byte[] { 1 };
    public ProgrammingSessionBehavior ProgrammingSession => ProgrammingSessionBehavior.BypassAll;

    public void GenerateSeed(byte level, Span<byte> seedBuffer, out int seedLength)
    {
        if (fixedSeed is not null)
        {
            fixedSeed.AsSpan().CopyTo(seedBuffer);
        }
        else
        {
            Random.Shared.NextBytes(seedBuffer.Slice(0, 2));
            // Avoid an all-zero seed - the generic module treats seed-all-zero
            // as "already unlocked", which would confuse a tester that just
            // asked for a fresh seed on a locked ECU.
            if (seedBuffer[0] == 0 && seedBuffer[1] == 0) seedBuffer[0] = 1;
        }
        seedLength = 2;
    }

    public bool ComputeExpectedKey(byte level, ReadOnlySpan<byte> seed, Span<byte> keyBuffer, out int keyLength)
    {
        if (level != 1 || seed.Length != 2)
        {
            keyLength = 0;
            return false;
        }
        ushort s = (ushort)((seed[0] << 8) | seed[1]);
        ushort k = ComputeKey(s);
        keyBuffer[0] = (byte)(k >> 8);
        keyBuffer[1] = (byte)(k & 0xFF);
        keyLength = 2;
        return true;
    }

    public void LoadConfig(JsonElement? config)
    {
        fixedSeed = null;
        if (config is null || config.Value.ValueKind != JsonValueKind.Object) return;
        if (!config.Value.TryGetProperty("fixedSeed", out var prop)) return;
        if (prop.ValueKind != JsonValueKind.String) return;
        if (TryParseHex16(prop.GetString(), out var hi, out var lo))
            fixedSeed = new[] { hi, lo };
    }

    /// <summary>The bare math. Exposed for unit tests and brute-forcers.</summary>
    public static ushort ComputeKey(ushort seed)
    {
        int n = (seed + 0xB0D8) & 0xFFFF;
        n = (-n) & 0xFFFF;                      // two's-complement negate (16-bit)
        int rol3 = ((n << 3) | (n >> 13)) & 0xFFFF;
        // gett43key byte-swaps the 16-bit result before formatting as hex; the
        // caller then writes bytes high-first onto the wire. Returning the
        // byte-swapped value keeps the (key >> 8, key & 0xFF) convention used
        // by E38 and matches the wire ordering on real hardware.
        return (ushort)(((rol3 & 0xFF) << 8) | ((rol3 >> 8) & 0xFF));
    }

    private static bool TryParseHex16(string? hex, out byte hi, out byte lo)
    {
        hi = 0; lo = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex!.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length != 4) return false;
        return byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hi)
            && byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out lo);
    }
}
