using System.Text.Json;
using Common.Protocol;

namespace Core.Security.Modules;

// The workhorse $27 module. Implements the full GMW3110-2010 SecurityAccess
// protocol envelope on top of an injected ISeedKeyAlgorithm — most users
// only need to write a small algorithm class and let this module handle:
//   - SID / length / subfunction-byte validation (NRC $12)
//   - Odd/even subfunction parity → requestSeed vs sendKey, level derivation
//   - Supported-level filtering (algo.SupportedLevels, NRC $12 otherwise)
//   - Seed-all-zero short-circuit when already unlocked at the level
//   - Pending-seed tracking ($22 if sendKey arrives without matching requestSeed)
//   - Failed-attempt counter with 3-strike lockout (NRC $35 / $36 / $37)
//   - Self-healing lockout: deadline timestamp compared against ctx.NowMs;
//     no scheduled timer required.
//   - EcuNode.BypassSecurity: when set, every $27 step short-circuits to a
//     positive response regardless of the algorithm. Models real ECUs whose
//     $27 level does no real validation (e.g. T43 TCM at level 1 per HP
//     Tuners' "no unlock service required" position).
//   - Algorithm policy ProgrammingSessionBehavior.BypassAll: same short-
//     circuit but driven automatically when
//     NodeState.SecurityProgrammingShortcutActive is true (set by either
//     $10 $02 directly or by the full GMW3110 $28 + $A5 chain). Models the
//     T43 boot-block stub (file offset 0x2BBFC in a 24264923 image) that
//     6Speed.T43 actually targets, without forcing the user to toggle
//     BypassSecurity manually before each programming-mode test. Algorithms
//     with the default ProgrammingSessionBehavior keep enforcing real seed/
//     key in programming session (E38, E67, etc.).
//
// Locking: every Handle() call takes ctx.State.Sync for the duration of the
// step so concurrent J2534 channels targeting the same ECU don't interleave
// security mutations.
public sealed class Gmw3110_2010_Generic : ISecurityAccessModule
{
    private const int LockoutDurationMs = 10_000;        // GMW3110-2010 §8 SecurityAccess — verify exact wording against your PDF
    private const int MaxAttemptsBeforeLockout = 3;

    private readonly ISeedKeyAlgorithm algorithm;

    public string Id { get; }

    public Gmw3110_2010_Generic(ISeedKeyAlgorithm algorithm, string? id = null)
    {
        this.algorithm = algorithm;
        Id = id ?? $"gmw3110-2010/{algorithm.Id}";
    }

    public void LoadConfig(JsonElement? config) => algorithm.LoadConfig(config);

    public void Handle(SecurityAccessContext ctx)
    {
        var payload = ctx.UsdtPayload;
        if (payload.Length < 2 || payload[0] != Service.SecurityAccess)
        {
            ctx.Egress.SendNegativeResponse(Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }
        byte sub = payload[1];
        if (sub == 0x00 || sub == 0x7F)
        {
            ctx.Egress.SendNegativeResponse(Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        bool isRequestSeed = (sub & 0x01) == 1;
        byte level = (byte)((sub + 1) >> 1);

        // Bypass short-circuits the algorithm entirely. Skips the level set,
        // the lockout state, the seed/key validation, the failed-attempt
        // counter - the wire trace still shows the normal two-step handshake
        // (positive seed response, positive sendKey response) but nothing is
        // actually validated. Used to model stub-security ECUs.
        //
        // Two entry paths:
        //   1. EcuNode.BypassSecurity: manual UI override.
        //   2. ProgrammingModeActive + algorithm's ProgrammingSession ==
        //      BypassAll: automatic, models the T43 boot-block stub once
        //      $10 $02 (or the full $28+$A5 chain) has put the ECU into a
        //      programming session.
        bool bypassByPolicy = ctx.State.SecurityProgrammingShortcutActive
                              && algorithm.ProgrammingSession == ProgrammingSessionBehavior.BypassAll;
        if (ctx.Node.BypassSecurity || bypassByPolicy)
        {
            string reason = ctx.Node.BypassSecurity
                ? "BypassSecurity flag"
                : "programming session + algorithm BypassAll policy";
            HandleBypass(ctx, sub, level, isRequestSeed, reason);
            return;
        }

        // Algorithm gatekeeps the level set.
        bool levelOk = false;
        foreach (var s in algorithm.SupportedLevels)
        {
            if (s == level) { levelOk = true; break; }
        }
        if (!levelOk)
        {
            ctx.Egress.SendNegativeResponse(Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        var state = ctx.State;
        lock (state.Sync)
        {
            if (state.IsInLockout(ctx.NowMs))
            {
                ctx.Egress.SendNegativeResponse(Nrc.RequiredTimeDelayNotExpired);
                return;
            }
            // Lockout deadline has elapsed but counters weren't reset — reset now.
            if (state.SecurityLockoutUntilMs != 0)
            {
                state.SecurityLockoutUntilMs = 0;
                state.SecurityFailedAttempts = 0;
            }

            if (isRequestSeed) HandleRequestSeed(ctx, sub, level);
            else               HandleSendKey   (ctx, sub, level);
        }
    }

    private void HandleBypass(SecurityAccessContext ctx, byte sub, byte level, bool isRequestSeed, string reason)
    {
        var state = ctx.State;
        lock (state.Sync)
        {
            if (isRequestSeed)
            {
                var zeros = new byte[Math.Max(1, algorithm.SeedLength)];
                state.SecurityPendingSeedLevel = level;
                state.SecurityLastIssuedSeed = zeros;
                ctx.Channel.Bus?.LogDiagnostic?.Invoke(
                    $"[$27 BYPASS] ECU '{ctx.Node.Name}' requestSeed sub=${sub:X2} level={level} -> seed=00 00 ({reason})");
                ctx.Egress.SendPositiveResponse(sub, zeros);
            }
            else
            {
                state.SecurityUnlockedLevel = level;
                state.SecurityPendingSeedLevel = 0;
                state.SecurityLastIssuedSeed = null;
                state.SecurityFailedAttempts = 0;
                state.SecurityLockoutUntilMs = 0;
                ctx.Channel.Bus?.LogDiagnostic?.Invoke(
                    $"[$27 BYPASS] ECU '{ctx.Node.Name}' sendKey sub=${sub:X2} level={level} -> unlocked, key ignored ({reason})");
                ctx.Egress.SendPositiveResponse(sub, ReadOnlySpan<byte>.Empty);
            }
        }
    }

    private void HandleRequestSeed(SecurityAccessContext ctx, byte sub, byte level)
    {
        // requestSeed payload is exactly SID + sub.
        if (ctx.UsdtPayload.Length != 2)
        {
            ctx.Egress.SendNegativeResponse(Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        var state = ctx.State;

        // Already unlocked at (or above) this level — return seed-all-zero.
        // Per the spec this is the signal "no further authentication needed".
        if (state.IsUnlocked(level))
        {
            var zeros = new byte[Math.Max(1, algorithm.SeedLength)];
            ctx.Egress.SendPositiveResponse(sub, zeros);
            return;
        }

        int bufLen = Math.Max(1, algorithm.SeedLength);
        Span<byte> seedBuf = bufLen <= 32 ? stackalloc byte[bufLen] : new byte[bufLen];
        algorithm.GenerateSeed(level, seedBuf, out int seedLen);
        if (seedLen <= 0 || seedLen > bufLen)
        {
            // Algorithm misbehaviour — treat as "no seed available".
            ctx.Egress.SendNegativeResponse(Nrc.ConditionsNotCorrectOrSequenceError);
            return;
        }

        var seedBytes = seedBuf.Slice(0, seedLen).ToArray();
        state.SecurityPendingSeedLevel = level;
        state.SecurityLastIssuedSeed = seedBytes;

        ctx.Egress.SendPositiveResponse(sub, seedBytes);
    }

    private void HandleSendKey(SecurityAccessContext ctx, byte sub, byte level)
    {
        // sendKey payload is SID + sub + key bytes.
        int keyLen = ctx.UsdtPayload.Length - 2;
        int maxKeyLen = Math.Max(1, algorithm.KeyLength);
        if (keyLen <= 0 || keyLen > maxKeyLen)
        {
            ctx.Egress.SendNegativeResponse(Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }
        var providedKey = ctx.UsdtPayload.Slice(2, keyLen);

        var state = ctx.State;

        // sendKey requires a prior requestSeed for the SAME level.
        if (state.SecurityPendingSeedLevel != level || state.SecurityLastIssuedSeed is null)
        {
            ctx.Egress.SendNegativeResponse(Nrc.ConditionsNotCorrectOrSequenceError);
            return;
        }

        Span<byte> expectedBuf = maxKeyLen <= 32 ? stackalloc byte[maxKeyLen] : new byte[maxKeyLen];
        bool produced = algorithm.ComputeExpectedKey(level, state.SecurityLastIssuedSeed, expectedBuf, out int expectedLen);
        if (!produced)
        {
            CountFailedAttempt(ctx);
            return;
        }

        bool match = expectedLen == keyLen
                     && providedKey.SequenceEqual(expectedBuf.Slice(0, expectedLen));
        if (!match)
        {
            CountFailedAttempt(ctx);
            return;
        }

        // Success: unlock this level, clear pending seed and counters.
        state.SecurityUnlockedLevel = level;
        state.SecurityPendingSeedLevel = 0;
        state.SecurityLastIssuedSeed = null;
        state.SecurityFailedAttempts = 0;

        ctx.Egress.SendPositiveResponse(sub, ReadOnlySpan<byte>.Empty);
    }

    private static void CountFailedAttempt(SecurityAccessContext ctx)
    {
        var state = ctx.State;
        state.SecurityFailedAttempts++;
        if (state.SecurityFailedAttempts >= MaxAttemptsBeforeLockout)
        {
            state.SecurityLockoutUntilMs = ctx.NowMs + LockoutDurationMs;
            // Lockout invalidates any pending seed — tester must requestSeed
            // again after the deadline expires.
            state.SecurityPendingSeedLevel = 0;
            state.SecurityLastIssuedSeed = null;
            ctx.Egress.SendNegativeResponse(Nrc.ExceededNumberOfAttempts);
            return;
        }
        ctx.Egress.SendNegativeResponse(Nrc.InvalidKey);
    }
}
