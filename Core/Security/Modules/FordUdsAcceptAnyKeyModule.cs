using Common.Protocol;
using System.Globalization;
using System.Text.Json;

namespace Core.Security.Modules;

// Ford UDS $27 SecurityAccess module for the ford-uds persona's flash path.
//
// Why a dedicated module rather than reusing Gmw3110_2010_Generic:
//   - Strict mode needs the real seed/key cipher to byte-compare the tester's
//     key. We do NOT have PCMTec's seed/key algorithm for this PCM, and the
//     project's donor-walker rule (feedback_no_os_pn_database) forbids inventing
//     one without a source - so a Strict module would NRC $35 every real key.
//   - BypassAll on the generic module always emits an ALL-ZERO seed and
//     auto-unlocks at requestSeed time (the DPS/CCRT "already unlocked, skip
//     sendKey" convention). That convention is GM-side; a Ford tester that
//     expects a real seed and a sendKey round-trip may reject a zero seed.
//
// So this module sits in the middle: it issues a real (non-zero) seed, lets the
// tester compute whatever key its own algorithm produces, and then ACCEPTS ANY
// KEY for that level. The wire trace looks exactly like a genuine two-step
// handshake (positive seed, positive sendKey); only the key comparison is
// skipped. That is enough to walk PCMTec past $27 and into the flash-write
// services so we can capture and implement them. It is honestly a bypass - the
// Behaviour property reports BypassAll so the UI shows $27 is not enforced.
//
// Configuration (SecurityModuleConfig JSON, all optional):
//   { "seedLength": 3,            // bytes in the issued seed (1..32, default 3)
//     "fixedSeed": "AFBB7F" }     // hex, exactly 2*seedLength chars - repeatable
//                                 //   seed for tests; absent -> random per request
//
// The default seed width is 3 bytes (24-bit): the FG Falcon PCM returns a 3-byte
// seed and expects a 3-byte key. Confirmed against the Spanish Oak flash tool's
// $27 handler (Spanish Oak Flash Tool/Sample/0x27 requestSecurityAccess.cs):
// requestSeed sub $01 -> response "67 01 AF BB 7F", the tester reads exactly 3
// seed bytes (buf3/buf4/buf5) and replies sendKey sub $02 with a 3-byte key from
// KeyGenMkI(seed, 08 30 61 A4 C5). A wrong-width seed makes the real tool (PCMTec)
// abort with "Incorrect seed length received". Because we accept the key
// unchecked, only the 3-byte seed length has to be right. If a future capture
// shows PCMTec using the actual KeyGenMkI algorithm and we want strict
// enforcement, this module can be swapped for a strict cipher implementing it.
public sealed class FordUdsAcceptAnyKeyModule : ISecurityAccessModule
{
    private const int DefaultSeedLength = 3;

    private int seedLength = DefaultSeedLength;
    private byte[]? fixedSeed;

    public FordUdsAcceptAnyKeyModule(string? id = null)
    {
        Id = id ?? "ford-uds-accept-any";
    }

    public string Id { get; }

    // Honest about what this does: $27 runs the handshake but never validates
    // the key, so the operator sees "not enforced" in the security tab.
    public SecurityModuleBehaviour Behaviour => SecurityModuleBehaviour.BypassAll;

    public void Handle(SecurityAccessContext ctx)
    {
        var payload = ctx.UsdtPayload;
        if (payload.Length < 2 || payload[0] != Service.SecurityAccess)
        {
            ctx.Egress.SendNegativeResponse(Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }
        byte sub = payload[1];
        // 0x00 and 0x7F are reserved subfunctions - never a real requestSeed/sendKey.
        if (sub == 0x00 || sub == 0x7F)
        {
            ctx.Egress.SendNegativeResponse(Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        // Odd subfunction = requestSeed, even = sendKey. Level is the 1-based
        // pair index ($01/$02 -> level 1, $03/$04 -> level 2, ...), matching
        // Gmw3110_2010_Generic so both modules derive levels identically.
        bool isRequestSeed = (sub & 0x01) == 1;
        byte level = (byte)((sub + 1) >> 1);

        var state = ctx.State;
        lock (state.Sync)
        {
            if (isRequestSeed) HandleRequestSeed(ctx, sub, level);
            else               HandleSendKey(ctx, sub, level);
        }
    }

    private void HandleRequestSeed(SecurityAccessContext ctx, byte sub, byte level)
    {
        // requestSeed payload is exactly SID + sub - any trailing bytes are malformed.
        if (ctx.UsdtPayload.Length != 2)
        {
            ctx.Egress.SendNegativeResponse(Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        var state = ctx.State;

        // Always issue a fresh seed and record it as pending, even when this (or a
        // higher) level is already unlocked. The GM/DPS "already unlocked -> all-
        // zero seed, skip sendKey" convention is NOT honoured by the Ford flash
        // tool: after unlocking level 2 it runs a full level-1 requestSeed/sendKey
        // round-trip, and a zero seed with no pending state made the follow-up
        // sendKey fail with NRC $22. Since we accept any key, re-issuing a real
        // seed here is harmless and keeps the handshake working at every level.
        var seed = GenerateSeed();
        state.SecurityPendingSeedLevel = level;
        state.SecurityLastIssuedSeed = seed;

        ctx.Channel.Bus?.LogSim?.Invoke(
            $"[$27 ACCEPT-ANY] ECU '{ctx.Node.Name}' requestSeed sub=${sub:X2} level={level} -> seed={Hex(seed)} (key will be accepted unchecked)");
        ctx.Egress.SendPositiveResponse(sub, seed);
    }

    private void HandleSendKey(SecurityAccessContext ctx, byte sub, byte level)
    {
        int keyLen = ctx.UsdtPayload.Length - 2;
        if (keyLen <= 0)
        {
            ctx.Egress.SendNegativeResponse(Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        var state = ctx.State;

        // sendKey requires a prior requestSeed for the SAME level - reject an
        // out-of-sequence key the way a real ECU does (NRC $22), so a tester
        // that skips the seed step still sees spec-correct behaviour.
        if (state.SecurityPendingSeedLevel != level || state.SecurityLastIssuedSeed is null)
        {
            ctx.Egress.SendNegativeResponse(Nrc.ConditionsNotCorrectOrSequenceError);
            return;
        }

        // Accept-any: the key bytes are not checked against any algorithm. Unlock
        // the level, clear the pending seed, and acknowledge. Keep the highest
        // level reached so a level-1 unlock that follows a level-2 unlock doesn't
        // downgrade access (the single-byte model treats higher as implying lower).
        state.SecurityUnlockedLevel = Math.Max(state.SecurityUnlockedLevel, level);
        state.SecurityPendingSeedLevel = 0;
        state.SecurityLastIssuedSeed = null;
        state.SecurityFailedAttempts = 0;
        state.SecurityLockoutUntilMs = 0;

        ctx.Channel.Bus?.LogSim?.Invoke(
            $"[$27 ACCEPT-ANY] ECU '{ctx.Node.Name}' sendKey sub=${sub:X2} level={level} -> unlocked, key accepted unchecked");
        ctx.Egress.SendPositiveResponse(sub, ReadOnlySpan<byte>.Empty);
    }

    private byte[] GenerateSeed()
    {
        if (fixedSeed is not null) return (byte[])fixedSeed.Clone();

        var seed = new byte[seedLength];
        Random.Shared.NextBytes(seed);
        // Never hand back an all-zero seed: IsUnlocked-style logic treats zero as
        // "already unlocked", and some testers reject a zero seed outright.
        bool allZero = true;
        for (int i = 0; i < seed.Length; i++)
        {
            if (seed[i] != 0) { allZero = false; break; }
        }
        if (allZero) seed[0] = 1;
        return seed;
    }

    public void LoadConfig(JsonElement? config)
    {
        seedLength = DefaultSeedLength;
        fixedSeed = null;
        if (config is null || config.Value.ValueKind != JsonValueKind.Object) return;

        if (config.Value.TryGetProperty("seedLength", out var lenProp)
            && lenProp.ValueKind == JsonValueKind.Number
            && lenProp.TryGetInt32(out int len)
            && len >= 1 && len <= 32)
        {
            seedLength = len;
        }

        if (config.Value.TryGetProperty("fixedSeed", out var seedProp)
            && seedProp.ValueKind == JsonValueKind.String)
        {
            fixedSeed = ParseHex(seedProp.GetString(), seedLength);
        }
    }

    // Parse a hex string of exactly expectedLen bytes (whitespace / 0x prefix
    // tolerated); returns null - and so falls back to a random seed - on any
    // length or format mismatch rather than throwing during config load.
    private static byte[]? ParseHex(string? hex, int expectedLen)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.IndexOfAny(new[] { ' ', '\t' }) >= 0)
            s = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (s.Length != expectedLen * 2) return null;
        var bytes = new byte[expectedLen];
        for (int i = 0; i < expectedLen; i++)
        {
            if (!byte.TryParse(s.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                               CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        }
        return bytes;
    }

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
