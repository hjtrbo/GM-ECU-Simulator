using System.Text.Json;

namespace Core.Security.Algorithms;

// Placeholder algorithm registered out of the box. Returns a deterministic
// non-zero seed and refuses every key, so every NRC path in the generic
// module (positive seed response → $35 invalid key → $36 lockout → $37 RTDNE
// → self-healing recovery) is exercisable end-to-end via a J2534 host without
// committing a real GM algorithm to source. Authors of real algorithms write
// their own ISeedKeyAlgorithm and register it in SecurityModuleRegistry.
//
// `GenerateSeed` writes [0x12, 0x34] — non-zero (so the generic module does
// NOT short-circuit to "already unlocked"), obvious on the wire when smoke-
// testing.
internal sealed class NotImplementedAlgorithm : ISeedKeyAlgorithm
{
    public string Id => "not-implemented";
    public int SeedLength => 2;
    public int KeyLength => 2;
    public IEnumerable<byte> SupportedLevels { get; } = new byte[] { 1 };

    public void GenerateSeed(byte level, Span<byte> seedBuffer, out int seedLength)
    {
        seedBuffer[0] = 0x12;
        seedBuffer[1] = 0x34;
        seedLength = 2;
    }

    public bool ComputeExpectedKey(byte level, ReadOnlySpan<byte> seed, Span<byte> keyBuffer, out int keyLength)
    {
        // No key acceptable. Generic module translates this to NRC $35.
        keyLength = 0;
        return false;
    }

    public void LoadConfig(JsonElement? config)
    {
        // No configuration — stub algorithm.
    }
}
