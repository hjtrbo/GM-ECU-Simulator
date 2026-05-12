using System.Globalization;
using System.Text.Json;

namespace Core.Security.Algorithms;

// GM E38 ECM seed-key algorithm (also known as GMLAN algorithm 0x92; the same
// algorithm is used by the E67 ECM). Two-byte seed, two-byte key, level 1 only.
//
// Algorithm source (documented openly in the GM tuning community):
//   - https://github.com/opensourcetuning/GM (file "E38 Seed Key Algorithm")
//   - https://github.com/YustasSwamp/gm-seed-key (table_gmlan.h)
//   - https://pcmhacking.net/forums/viewtopic.php?t=5876
//
// Reference C code:
//     k = byteSwap16(seed);
//     k = k + 0x7D58;
//     k = ~k;
//     k = k & 0xFFFF;
//     k = k + 0x8001;
//     key = byteSwap16(k & 0xFFFF);
//
// Verified test vectors (computed from the algorithm above):
//     0x1234 -> 0x96CE
//     0xA1B2 -> 0x0750
//     0xDEAD -> 0xCA54
//     0xCAFE -> 0xDE03
//     0xFFFF -> 0xA902
// See E38AlgorithmTests for the round-trip checks. Real captured pairs from
// hardware should be added there as additional Theory inputs when available.
//
// Configuration (optional, via SecurityModuleConfig JsonElement):
//     { "fixedSeed": "1234" }   // hex, 4 chars — locks the seed for repeatable testing
// No fixedSeed -> Random.Shared.NextBytes per request (more realistic).
public sealed class E38Algorithm : ISeedKeyAlgorithm
{
    private byte[]? fixedSeed;

    public string Id => "gm-e38";
    public int SeedLength => 2;
    public int KeyLength => 2;
    public IEnumerable<byte> SupportedLevels { get; } = new byte[] { 1 };

    public void GenerateSeed(byte level, Span<byte> seedBuffer, out int seedLength)
    {
        if (fixedSeed is not null)
        {
            fixedSeed.AsSpan().CopyTo(seedBuffer);
        }
        else
        {
            Random.Shared.NextBytes(seedBuffer.Slice(0, 2));
            // Avoid an all-zero seed — the generic module treats seed-all-zero
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
        uint k = (uint)((seed >> 8) | ((seed & 0xFF) << 8));
        k = k + 0x7D58;
        k = ~k;
        k = k & 0xFFFF;
        k = k + 0x8001;
        return (ushort)(((k & 0xFF00) >> 8) | ((k & 0xFF) << 8));
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
