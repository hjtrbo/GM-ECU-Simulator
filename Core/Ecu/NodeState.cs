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

    /// <summary>
    /// Per-node ISO 15765-2 sender. Drives the FF/FC/CF cascade for outbound
    /// USDT responses, observing the host's BS / STmin / FC.WAIT / FC.OVFLW
    /// per §9.6.5. Replaced the legacy static blast-CFs implementation in
    /// May 2026 to match dealer-tool throttling expectations.
    /// </summary>
    public IsoTpFragmenter Fragmenter { get; } = new();

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

    // ----- $28 DisableNormalCommunication / $A5 ProgrammingMode / $34/$36 download state -----
    //
    // GMW3110-2010 §8.9 / §8.17 / §8.12 / §8.13. Each downstream service in
    // the programming chain checks the flag set by the previous one:
    //   $A5 $03 enableProgrammingMode requires NormalCommunicationDisabled
    //                                  AND a prior $A5 $01/$02 (ProgrammingModeRequested)
    //   $34 RequestDownload            requires SecurityUnlockedLevel > 0
    //                                  AND NormalCommunicationDisabled
    //                                  AND ProgrammingModeActive
    //   $36 TransferData               requires DownloadActive (set by $34)
    //
    // All four flags are cleared by EcuExitLogic.Run (called from $20 and from the
    // P3C TesterPresent timeout) so the ECU returns to "Normal Communication Mode".

    /// <summary>
    /// True after a $28 DisableNormalCommunication positive response. Cleared
    /// by $20 ReturnToNormalMode or P3C timeout via EcuExitLogic.
    /// </summary>
    public bool NormalCommunicationDisabled { get; set; }

    /// <summary>
    /// True after a $A5 sub $01 (requestProgrammingMode) or sub $02
    /// (requestProgrammingMode_HighSpeed) positive response - the prerequisite
    /// for the $A5 sub $03 enableProgrammingMode that actually enters the session.
    /// </summary>
    public bool ProgrammingModeRequested { get; set; }

    /// <summary>
    /// True after $A5 sub $03 enableProgrammingMode. Once set, $34
    /// RequestDownload is permitted (subject to security + $28).
    /// </summary>
    public bool ProgrammingModeActive { get; set; }

    /// <summary>True if the most recent $A5 was sub $02 (high-speed). Informational only.</summary>
    public bool ProgrammingHighSpeed { get; set; }

    /// <summary>
    /// True once $10 $02 InitiateDiagnosticOperation has been accepted, OR once
    /// the full GMW3110 $28 + $A5 $01/02 + $A5 $03 chain has completed (i.e.
    /// any time ProgrammingModeActive is true). Read by the $27 security
    /// module to decide whether an algorithm whose
    /// <see cref="Core.Security.ProgrammingSessionBehavior"/> is
    /// <c>BypassAll</c> (T43 boot-block stub) should short-circuit. Distinct
    /// from <see cref="ProgrammingModeActive"/> because $10 $02 is a UDS-style
    /// shortcut into programming session for security purposes only - it must
    /// NOT bypass the $A5 sequence-error checks that gate $34
    /// RequestDownload. Cleared by <see cref="ClearProgrammingState"/>.
    /// </summary>
    public bool SecurityProgrammingShortcutActive { get; set; }

    /// <summary>
    /// True between a successful $34 RequestDownload and the end of the
    /// programming session (P3C timeout, $20, or another $34 - same-session
    /// repeated $34 is allowed per §8.12 once the previous transfer is complete,
    /// but a $34 received WHILE this is true is a sequence error).
    /// </summary>
    public bool DownloadActive { get; set; }

    /// <summary>
    /// uncompressedMemorySize from the most recent $34. Used for sanity bounds
    /// on $36 startingAddress + dataRecord ranges (§8.13.4 NRC $31 ROOR).
    /// </summary>
    public uint DownloadDeclaredSize { get; set; }

    /// <summary>
    /// Sink buffer for the running programming session. Allocated to
    /// DownloadDeclaredSize on $34. Each $36 writes its dataRecord at the
    /// startingAddress offset. Tests inspect this to verify a download
    /// payload round-tripped intact.
    /// </summary>
    public byte[]? DownloadBuffer { get; set; }

    /// <summary>
    /// Total bytes the buffer has accepted across all $36 calls in this
    /// session - distinct from DownloadDeclaredSize which is the upper bound.
    /// </summary>
    public uint DownloadBytesReceived { get; set; }

    /// <summary>
    /// Number of bytes in the startingAddress field for this ECU. GMW3110
    /// §8.13 requires it to be consistent across all $36 calls to the same
    /// node. 3 is the typical default; the value is fixed at $34 time and
    /// applies to all subsequent $36s in the session.
    /// </summary>
    public int DownloadAddressByteCount { get; set; } = 3;

    /// <summary>
    /// startingAddress of the first $36 in the current session, used as the
    /// base when capture mode is on (real GM hosts send absolute RAM/flash
    /// addresses like $003FB800; we store everything relative to that). Set
    /// on the first $36, cleared by ClearProgrammingState. Null = no $36
    /// has arrived yet in this session.
    /// </summary>
    public uint? DownloadCaptureBaseAddress { get; set; }

    /// <summary>
    /// Wipes all programming + download flags. Called from EcuExitLogic so $20
    /// and P3C timeout return the ECU to Normal Communication Mode.
    /// </summary>
    public void ClearProgrammingState()
    {
        NormalCommunicationDisabled = false;
        ProgrammingModeRequested = false;
        ProgrammingModeActive = false;
        ProgrammingHighSpeed = false;
        SecurityProgrammingShortcutActive = false;
        DownloadActive = false;
        DownloadDeclaredSize = 0;
        DownloadBuffer = null;
        DownloadBytesReceived = 0;
        DownloadCaptureBaseAddress = null;
    }
}
