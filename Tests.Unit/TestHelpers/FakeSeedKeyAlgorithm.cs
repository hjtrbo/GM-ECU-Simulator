using System.Text.Json;
using Core.Security;

namespace EcuSimulator.Tests.TestHelpers;

// Algorithm fake whose seed and expected-key bytes are set directly by the
// test. ComputeKeySucceeds == false → ComputeExpectedKey returns false so the
// generic module emits NRC $35 (drives the invalid-key / lockout path with a
// tester-controlled key).
internal sealed class FakeSeedKeyAlgorithm : ISeedKeyAlgorithm
{
    public string Id { get; init; } = "fake";
    public int SeedLength { get; init; } = 2;
    public int KeyLength { get; init; } = 2;
    public IEnumerable<byte> SupportedLevels { get; init; } = new byte[] { 1 };

    public byte[] SeedToReturn { get; set; } = new byte[] { 0x12, 0x34 };
    public byte[] ExpectedKey { get; set; } = new byte[] { 0xAB, 0xCD };
    public bool ComputeKeySucceeds { get; set; } = true;

    public void GenerateSeed(byte level, Span<byte> seedBuffer, out int seedLength)
    {
        SeedToReturn.AsSpan().CopyTo(seedBuffer);
        seedLength = SeedToReturn.Length;
    }

    public bool ComputeExpectedKey(byte level, ReadOnlySpan<byte> seed, Span<byte> keyBuffer, out int keyLength)
    {
        if (!ComputeKeySucceeds)
        {
            keyLength = 0;
            return false;
        }
        ExpectedKey.AsSpan().CopyTo(keyBuffer);
        keyLength = ExpectedKey.Length;
        return true;
    }

    public void LoadConfig(JsonElement? config) { /* not under test */ }
}
