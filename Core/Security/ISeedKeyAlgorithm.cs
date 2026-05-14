using System.Text.Json;

namespace Core.Security;

// The seed/key algorithm strategy injected into Gmw3110_2010_Generic.
// Implementations supply only the math — the generic module handles
// every protocol-envelope concern (length validation, subfunction parity,
// pending-seed tracking, NRC dispatch, lockout / attempt counting,
// P3C interaction).
//
// Buffers passed by Span<byte> are sized to SeedLength / KeyLength;
// implementations write into them in-place and return the actual length
// they used (≤ the buffer size).
public interface ISeedKeyAlgorithm
{
    /// <summary>Stable identifier (informational — selection happens at the module level).</summary>
    string Id { get; }

    /// <summary>Maximum seed length in bytes the generic module should buffer for. 2 is typical for GMW3110.</summary>
    int SeedLength { get; }

    /// <summary>Maximum key length in bytes the generic module should expect from the tester. Often equal to SeedLength.</summary>
    int KeyLength { get; }

    /// <summary>Security levels (1-based, odd-subfunction-derived) this algorithm supports.</summary>
    IEnumerable<byte> SupportedLevels { get; }

    /// <summary>
    /// How this algorithm behaves once the ECU has entered a programming
    /// session (either via $10 $02 or via the full $28 + $A5 chain).
    /// Default <see cref="ProgrammingSessionBehavior.UnchangedAlgorithm"/> -
    /// the same seed/key math runs regardless of session. Algorithms that
    /// model a permissive boot-block stub (e.g. T43 TCM) override to
    /// <see cref="ProgrammingSessionBehavior.BypassAll"/>; the generic module
    /// then short-circuits $27 to seed = 00 / any-key accepted whenever
    /// <see cref="Core.Ecu.NodeState.SecurityProgrammingShortcutActive"/> is
    /// true.
    /// </summary>
    ProgrammingSessionBehavior ProgrammingSession => ProgrammingSessionBehavior.UnchangedAlgorithm;

    /// <summary>
    /// Generate a seed for the requested level into the supplied buffer.
    /// Write `seedLength` bytes (≤ SeedLength); the generic module will
    /// emit those bytes verbatim in the positive response.
    /// </summary>
    void GenerateSeed(byte level, Span<byte> seedBuffer, out int seedLength);

    /// <summary>
    /// Compute the key the tester is expected to send back for the given
    /// (level, seed) pair. Return true and fill `keyBuffer` with `keyLength`
    /// bytes on success. Return false when this algorithm cannot produce a
    /// key (the generic module treats this as NRC $35 InvalidKey).
    /// </summary>
    bool ComputeExpectedKey(byte level, ReadOnlySpan<byte> seed, Span<byte> keyBuffer, out int keyLength);

    /// <summary>Apply algorithm-specific configuration (e.g. fixed-key bytes, constants). May be null.</summary>
    void LoadConfig(JsonElement? config);
}
