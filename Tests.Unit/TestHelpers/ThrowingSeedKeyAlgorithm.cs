using System.Text.Json;
using Core.Security;

namespace EcuSimulator.Tests.TestHelpers;

// Algorithm fake that throws on demand from either GenerateSeed or
// ComputeExpectedKey. Used by chaos tests to verify the bus's
// exception-isolation wrapper translates a thrown algorithm into an NRC
// rather than crashing the dispatch thread.
internal sealed class ThrowingSeedKeyAlgorithm : ISeedKeyAlgorithm
{
    public string Id { get; init; } = "throwing";
    public int SeedLength { get; init; } = 2;
    public int KeyLength { get; init; } = 2;
    public IEnumerable<byte> SupportedLevels { get; init; } = new byte[] { 1 };

    public bool ThrowOnGenerateSeed { get; set; }
    public bool ThrowOnComputeKey { get; set; }

    public void GenerateSeed(byte level, Span<byte> seedBuffer, out int seedLength)
    {
        if (ThrowOnGenerateSeed)
            throw new InvalidOperationException("simulated algorithm fault on GenerateSeed");
        // Default benign seed if we're not throwing.
        seedBuffer[0] = 0x12;
        seedBuffer[1] = 0x34;
        seedLength = 2;
    }

    public bool ComputeExpectedKey(byte level, ReadOnlySpan<byte> seed, Span<byte> keyBuffer, out int keyLength)
    {
        if (ThrowOnComputeKey)
            throw new InvalidOperationException("simulated algorithm fault on ComputeExpectedKey");
        keyBuffer[0] = 0xAB;
        keyBuffer[1] = 0xCD;
        keyLength = 2;
        return true;
    }

    public void LoadConfig(JsonElement? config) { /* not under test */ }
}
