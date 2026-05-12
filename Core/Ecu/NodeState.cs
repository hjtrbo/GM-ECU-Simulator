using System.Collections.Concurrent;
using Core.Bus;
using Core.Transport;

namespace Core.Ecu;

// Per-ECU runtime diagnostic state. Anything a service mutates or creates
// at runtime lives here (in contrast to EcuNode, which carries user config
// like identity, glitch settings, and the PID list). A single container
// lets future services (e.g. download $34/$36, programming session,
// routine control) compose precondition checks like
// "ECU must be unlocked at level N" by reading the same object $27 writes.
//
// Heterogeneous synchronisation by design:
//  - Dpids: ConcurrentDictionary, lock-free.
//  - LastEnhancedChannel: Volatile.Read/Write + CAS via Interlocked.
//  - TesterPresent / Reassembler: each carries its own internal locking.
//  - DynamicallyDefinedPids and the new $27 security fields: guarded by
//    the Sync lock below — they have no per-field synchronisation.
//
// Initial state on construction matches "Normal Communication Mode" /
// default diagnostic session per GMW3110-2010 — identical to the state
// after $20 ReturnToNormalMode (with the exception that on power-on the
// security lockout/attempt counters reset; $20 typically leaves them
// alone). Sections to verify in your copy of the spec: §6.2 (P3C on
// power-on), §8 ReturnToNormalMode entry, §8 SecurityAccess entry.
public sealed class NodeState
{
    // ----- Migrated from EcuNode (semantics unchanged) -----

    public ConcurrentDictionary<byte, Dpid> Dpids { get; } = new();

    public HashSet<uint> DynamicallyDefinedPids { get; } = new();

    public IsoTpReassembler Reassembler { get; } = new();

    public TesterPresentState TesterPresent { get; } = new();

    private ChannelSession? lastEnhancedChannel;
    public ChannelSession? LastEnhancedChannel
    {
        get => Volatile.Read(ref lastEnhancedChannel);
        set => Volatile.Write(ref lastEnhancedChannel, value);
    }

    /// <summary>
    /// Clears LastEnhancedChannel only if it currently points to the given
    /// session. Used on host disconnect so the next P3C/$20 exit doesn't
    /// enqueue an unsolicited $60 onto an orphaned channel. Atomic via CAS.
    /// </summary>
    public void ClearLastEnhancedChannelIf(ChannelSession ch)
        => Interlocked.CompareExchange(ref lastEnhancedChannel, null, ch);

    public void AddDpid(Dpid dpid) => Dpids[dpid.Id] = dpid;

    // ----- $27 SecurityAccess state -----
    //
    // All security fields default to the post-power-on state (locked at
    // every level, no pending seed, no failed attempts, no lockout).
    // Sync protects these — they have no per-field synchronisation.

    public readonly Lock Sync = new();

    /// <summary>Highest security level currently unlocked. 0 = fully locked.</summary>
    public byte SecurityUnlockedLevel { get; set; }

    /// <summary>Level that the last requestSeed was issued for. 0 = no seed outstanding.</summary>
    public byte SecurityPendingSeedLevel { get; set; }

    /// <summary>Bytes of the seed the ECU most recently issued. Null = no seed outstanding.</summary>
    public byte[]? SecurityLastIssuedSeed { get; set; }

    /// <summary>Count of consecutive failed sendKey attempts since the last successful unlock or lockout-deadline expiry.</summary>
    public int SecurityFailedAttempts { get; set; }

    /// <summary>NowMs value at which the lockout expires. 0 = no active lockout.</summary>
    public long SecurityLockoutUntilMs { get; set; }

    /// <summary>Opaque slot for the security module's own bookkeeping (e.g. derived-key cache).</summary>
    public object? SecurityModuleState { get; set; }

    // ----- Helpers for future services -----

    /// <summary>True if the ECU is currently unlocked at or above the given level.</summary>
    public bool IsUnlocked(byte level) => SecurityUnlockedLevel >= level && level != 0;

    /// <summary>True if the security-access lockout deadline has not yet elapsed.</summary>
    public bool IsInLockout(long nowMs) => SecurityLockoutUntilMs > nowMs;
}
