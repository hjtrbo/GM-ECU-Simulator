using System.Globalization;
using System.Text.Json;

namespace Core.Security.Algorithms;

// Descriptively-named permissive $27 algorithm for use during DPS / SPS
// programming sessions against the simulator.
//
// Why this exists separately from T43Algorithm:
//   * T43Algorithm's name ties it to the 6T70-family transmission controller.
//     Picking it for a non-TCM ECU in the UI dropdown reads as wrong.
//   * The real-life "permissive boot block" behaviour - return seed = 00 00,
//     accept any key - is generic, not T43-specific. The T43 just happens to
//     ship that stub in its boot block (file offset 0x2BBFC in a 24264923
//     image); plenty of other GM ECUs do the same trick.
//
// Behaviour - identical to T43 in practice:
//   * ProgrammingSession = BypassAll. The generic module short-circuits both
//     seed generation (-> 00 00) and key verification (-> any key OK) as soon
//     as NodeState.SecurityProgrammingShortcutActive is true (set by either
//     $10 $02 directly or by the full GMW3110 $28 + $A5 $01 + $A5 $03 chain).
//   * Outside a programming session, the gett43key math (decompiled from the
//     FOSS-licensed 6Speed.T43 tester) runs unchanged. This means a tester
//     that hits $27 without first entering programming mode will see real
//     T43-style seed/key pairs - useful for repro of GM-style $27 flows.
//
// Configuration (optional, via SecurityModuleConfig JsonElement):
//     { "fixedSeed": "1234" }   // hex, 4 chars - locks the operational-mode
//                               //                seed for repeatable testing
public sealed class Gmw3110ProgrammingBypassAlgorithm : ISeedKeyAlgorithm
{
    private byte[]? fixedSeed;

    public string Id => "gmw3110-programming-bypass";
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
        ushort k = T43Algorithm.ComputeKey(s);
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
