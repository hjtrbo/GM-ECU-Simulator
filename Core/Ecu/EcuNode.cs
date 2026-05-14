using System.Text.Json;
using Common.Glitch;
using Core.Security;

namespace Core.Ecu;

// One simulated ECU on the virtual GMLAN bus. All identity properties
// are mutable so the editor UI can rename / re-address an ECU live.
// The PID list is a lock-protected List with an event so the UI can
// rebuild its display when PIDs are added or removed.
//
// CAN ID convention: real OBD-II-compliant GM vehicles use the $7E0+ pair
// (USDT request $7E0..$7E7, response $7E8..$7EF). GMW3110's worked examples
// use $241/$641/$541 — pedagogical only. Defaults match the real hardware
// convention; tests that quote the spec's example IDs do so on purpose to
// keep the test bytes traceable to the spec tables.
public sealed class EcuNode
{
    public string Name { get; set; } = "";
    public ushort PhysicalRequestCanId { get; set; }
    public ushort UsdtResponseCanId { get; set; }
    public ushort UudtResponseCanId { get; set; }

    // When true the simulator drives $3E keepalives for hosts that delegate via
    // PassThruStartPeriodicMsg. When false the registration is accepted but no
    // timer is created — the P3C session will not be maintained for such hosts.
    public bool AllowPeriodicTesterPresent { get; set; } = true;

    // ISO 15765-2 Flow Control bytes emitted by this ECU's reassembler in
    // response to an inbound First Frame. The two-byte tail of the FC frame
    // (after the 0x30 CTS PCI byte) is `BS STmin`. Defaults are 0/0, which
    // matches the DataLogger's expectation and the most permissive ISO-TP
    // behaviour (send all CFs without further FC, no inter-frame delay).
    //
    // Override per ECU to mimic real silicon: e.g. the 6Speed.T43 tester
    // checks the FC bytes for the substring "01", so a TCM configured to
    // emit BS=1 (FC = 30 01 00) makes that pattern match and lets the
    // kernel-upload flow proceed.
    public byte FlowControlBlockSize { get; set; }
    public byte FlowControlSeparationTime { get; set; }

    // Number of bytes the host uses for the startingAddress field of $36
    // TransferData. Spec range is 2..4; 4 matches real T43-era ECUs and is
    // the default. Pass-through to NodeState - the runtime fast path reads
    // it via node.State.DownloadAddressByteCount, but keeping the editable
    // knob on EcuNode mirrors how every other per-ECU config field looks.
    public int DownloadAddressByteCount
    {
        get => State.DownloadAddressByteCount;
        set => State.DownloadAddressByteCount = value;
    }

    // GMW3110-2010 §8.16 ReportProgrammedState ($A2) value returned in the
    // positive response. Defaults to 0x00 FullyProgrammed - what a normal
    // running ECU reports. Other defined values:
    //   0x00 FP   fully programmed
    //   0x01 NSC  no op s/w or cal data
    //   0x02 NC   op s/w present, cal missing
    //   0x03 SDC  s/w present, default/no-start cal
    //   0x50 GMF  general memory fault
    //   0x51 RMF  RAM memory fault
    //   0x52 NVRMF NVRAM memory fault
    //   0x53 BMF  boot memory failure
    //   0x54 FMF  flash memory failure
    //   0x55 EEMF EEPROM memory failure
    public byte ProgrammedState { get; set; }

    // Per-ECU glitch-injection configuration. The data model exists so the
    // UI/persistence layers can edit and round-trip these settings; the actual
    // injection logic in Core/Services is NOT yet implemented.
    public GlitchConfig Glitch { get; set; } = GlitchConfig.CreateDefault();

    private readonly List<Pid> pids = new();
    private readonly Lock pidsLock = new();

    // GMW3110 §8.3 ReadDataByIdentifier ($1A) data. Each DID maps to a raw
    // byte array; the service handler returns [$5A, did, ...bytes] verbatim
    // so the user can configure any spec-defined identifier (VIN $90,
    // calibration ID $92, etc.) without the simulator interpreting the value.
    // Mutable through Set/RemoveIdentifier so the editor UI can hot-edit.
    private readonly Dictionary<byte, byte[]> identifiers = new();
    private readonly Lock identifiersLock = new();

    /// <summary>Raised after a PID is added, removed, or the list is cleared.</summary>
    public event EventHandler? PidsChanged;

    /// <summary>Raised after an identifier is added, replaced, removed, or cleared.</summary>
    public event EventHandler? IdentifiersChanged;

    // Per-ECU runtime state (Dpids, TesterPresent, Reassembler, security
    // session, $2D dynamic-PID set, LastEnhancedChannel, etc.) lives in
    // NodeState. EcuNode keeps user config; State carries runtime state.
    public NodeState State { get; } = new();

    // The chosen security module for this ECU (null = $27 returns NRC $11
    // ServiceNotSupported). Mutable so the editor can hot-swap modules at
    // runtime, mirroring how identity fields work. The module instance is
    // separate from NodeState: state is data, the module is behaviour.
    public ISecurityAccessModule? SecurityModule { get; set; }

    // Raw module-specific configuration as last loaded from disk or edited
    // in the UI. ConfigStore round-trips this verbatim; the module consumes
    // it via LoadConfig. EcuNode owns the blob so the JSON survives across
    // module hot-swaps and so saving doesn't require each module to remember
    // its own config separately.
    public JsonElement? SecurityModuleConfig { get; set; }

    // When true the active security module short-circuits every $27 step to
    // a positive response: requestSeed emits all-zeros, sendKey transitions
    // the level to unlocked, the algorithm is never invoked. Models
    // stub-security $27 levels that exist on real hardware (HP Tuners
    // documents T43 TCM as "no unlock service required" for tuning, which
    // is what the 6Speed.T43 tester depends on by hardcoding key 00 00).
    // Independent of SecurityModule selection - the module still has to be
    // configured (i.e. not (none)) so the bypass has a wrapper to short-
    // circuit; (none) keeps the spec-correct NRC $11.
    public bool BypassSecurity { get; set; }

    /// <summary>Snapshot copy of the PID list — safe to enumerate cross-thread.</summary>
    public IReadOnlyList<Pid> Pids
    {
        get { lock (pidsLock) return pids.ToArray(); }
    }

    public Pid? GetPid(uint address)
    {
        lock (pidsLock) return pids.FirstOrDefault(p => p.Address == address);
    }

    public void AddPid(Pid pid)
    {
        lock (pidsLock)
        {
            // Replace by address if one already exists — matches the prior
            // ConcurrentDictionary semantics that callers relied on.
            int existing = pids.FindIndex(p => p.Address == pid.Address);
            if (existing >= 0) pids[existing] = pid;
            else pids.Add(pid);
        }
        PidsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool RemovePid(Pid pid)
    {
        bool removed;
        lock (pidsLock) removed = pids.Remove(pid);
        if (removed) PidsChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public bool RemovePidByAddress(uint address)
    {
        bool removed;
        lock (pidsLock) removed = pids.RemoveAll(p => p.Address == address) > 0;
        if (removed) PidsChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>
    /// Notifies subscribers that the PID list has changed without adding/removing.
    /// Editor calls this after mutating a PID's properties so the live monitor
    /// refreshes its column data.
    /// </summary>
    public void RaisePidsChanged() => PidsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Snapshot copy of the identifier map — safe to enumerate cross-thread.</summary>
    public IReadOnlyDictionary<byte, byte[]> Identifiers
    {
        get { lock (identifiersLock) return identifiers.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone()); }
    }

    /// <summary>Looks up a $1A identifier. Returns null if the DID is not configured.</summary>
    public byte[]? GetIdentifier(byte did)
    {
        lock (identifiersLock) return identifiers.TryGetValue(did, out var data) ? (byte[])data.Clone() : null;
    }

    /// <summary>Sets (or replaces) the data for a $1A identifier. The bytes are copied.</summary>
    public void SetIdentifier(byte did, ReadOnlySpan<byte> data)
    {
        lock (identifiersLock) identifiers[did] = data.ToArray();
        IdentifiersChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveIdentifier(byte did)
    {
        bool removed;
        lock (identifiersLock) removed = identifiers.Remove(did);
        if (removed) IdentifiersChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }
}
