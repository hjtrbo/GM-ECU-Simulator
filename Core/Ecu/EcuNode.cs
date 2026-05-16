using System.Text.Json;
using Common.Glitch;
using Common.Protocol;
using Core.Ecu.Personas;
using Core.Security;

namespace Core.Ecu;

// One simulated ECU on the virtual GMLAN bus. All identity properties
// are mutable so the editor UI can rename / re-address an ECU live.
// The PID list is a lock-protected List with an event so the UI can
// rebuild its display when PIDs are added or removed.
//
// CAN ID convention: real OBD-II-compliant GM vehicles use the $7E0+ pair
// (USDT request $7E0..$7E7, response $7E8..$7EF). GMW3110's worked examples
// use $241/$641/$541 - pedagogical only. Defaults match the real hardware
// convention; tests that quote the spec's example IDs do so on purpose to
// keep the test bytes traceable to the spec tables.
public sealed class EcuNode
{
    public string Name { get; set; } = "";
    public ushort PhysicalRequestCanId { get; set; }
    public ushort UsdtResponseCanId { get; set; }
    public ushort UudtResponseCanId { get; set; }

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

    /// <summary>
    /// GMW3110 §8.16 SPS classification. Default <see cref="SpsType.A"/> matches a
    /// fully-programmed running ECU. SPS_TYPE_C activates the blank-ECU
    /// state machine: silent until $A2 received while $28 active, then
    /// responses go on SPS_PrimeRsp ($300 | <see cref="DiagnosticAddress"/>) and
    /// the ECU accepts SPS_PrimeReq ($000 | DiagnosticAddress). To use type C,
    /// set <see cref="PhysicalRequestCanId"/> = SPS_PrimeReq and
    /// <see cref="UsdtResponseCanId"/> = SPS_PrimeRsp so the normal bus
    /// routing reaches the ECU once it activates. GM SPS / DPS drives this
    /// flow during "Get Controller Info" to enumerate programmable ECUs.
    /// </summary>
    public SpsType SpsType { get; set; } = SpsType.A;

    /// <summary>
    /// GMW3110 8-bit diagnostic address used to derive SPS_PrimeReq/Rsp when
    /// <see cref="SpsType"/> is <see cref="SpsType.C"/>. Informational for
    /// SPS_TYPE_A/B (their request/response IDs are already permanent). For
    /// type C this should equal the low byte of PhysicalRequestCanId, e.g.
    /// $11 → request $011, response $311. The UI keeps both fields editable
    /// so users can verify the derivation rather than only see the CAN IDs.
    /// </summary>
    public byte DiagnosticAddress { get; set; }

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
    // Per-DID provenance tag (User / Bin / Auto). STICKY across RemoveIdentifier:
    // a user who blanks a value still owns that row, so a subsequent auto-
    // populate or merge-mode bin load won't overwrite the deliberate blank.
    // Cleared explicitly via ClearAllIdentifierSources during a Replace-all
    // bin load, which then re-marks every well-known DID as Bin even when
    // blank (the user explicitly opted into "this ECU is the bin's view now").
    // Guarded by the same identifiersLock for atomic value+source updates.
    private readonly Dictionary<byte, DidSource> identifierSources = new();
    private readonly Lock identifiersLock = new();

    /// <summary>Raised after a PID is added, removed, or the list is cleared.</summary>
    public event EventHandler? PidsChanged;

    /// <summary>Raised after an identifier is added, replaced, removed, or cleared.</summary>
    public event EventHandler? IdentifiersChanged;

    // Per-ECU runtime state (Dpids, TesterPresent, Reassembler, security
    // session, $2D dynamic-PID set, LastEnhancedChannel, etc.) lives in
    // NodeState. EcuNode keeps user config; State carries runtime state.
    public NodeState State { get; } = new();

    // The diagnostic dispatch table the ECU presents on the wire RIGHT NOW.
    // Defaults to GMW3110-2010 (what every stock ECU spends its life speaking).
    // Swapped to UdsKernelPersona by Service36Handler when $36 sub $80
    // DownloadAndExecute lands; reset back by EcuExitLogic on $20 / P3C
    // timeout. The persona is per-ECU, not per-channel: a kernel handover
    // changes what every tester on the bus sees from this ECU.
    public IDiagnosticPersona Persona { get; set; } = Gmw3110Persona.Instance;

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

    /// <summary>Snapshot copy of the PID list - safe to enumerate cross-thread.</summary>
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
            // Replace by address if one already exists - matches the prior
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

    /// <summary>Snapshot copy of the identifier map - safe to enumerate cross-thread.</summary>
    public IReadOnlyDictionary<byte, byte[]> Identifiers
    {
        get { lock (identifiersLock) return identifiers.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone()); }
    }

    /// <summary>
    /// Snapshot copy of the per-DID source map - safe to enumerate cross-thread.
    /// Includes entries that have no bytes (sticky blanks: source=User with
    /// the value cleared) so ConfigStore can preserve the sticky tag on save.
    /// </summary>
    public IReadOnlyDictionary<byte, DidSource> IdentifierSources
    {
        get { lock (identifiersLock) return new Dictionary<byte, DidSource>(identifierSources); }
    }

    /// <summary>Looks up a $1A identifier. Returns null if the DID is not configured.</summary>
    public byte[]? GetIdentifier(byte did)
    {
        lock (identifiersLock) return identifiers.TryGetValue(did, out var data) ? (byte[])data.Clone() : null;
    }

    /// <summary>
    /// Sets (or replaces) the data for a $1A identifier with an explicit
    /// provenance tag. The bytes are copied. Callers that don't track
    /// provenance use the single-arg overload, which defaults the source
    /// to <see cref="DidSource.User"/>.
    /// </summary>
    public void SetIdentifier(byte did, ReadOnlySpan<byte> data, DidSource source)
    {
        lock (identifiersLock)
        {
            identifiers[did] = data.ToArray();
            identifierSources[did] = source;
        }
        IdentifiersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Backwards-compatible overload that records the value as user-typed.
    /// New code that knows the provenance should call the three-arg overload
    /// with an explicit <see cref="DidSource"/>.
    /// </summary>
    public void SetIdentifier(byte did, ReadOnlySpan<byte> data)
        => SetIdentifier(did, data, DidSource.User);

    /// <summary>
    /// Removes the bytes for a DID but keeps its source tag. This is what
    /// makes "user typed then deleted" stay <see cref="DidSource.User"/>
    /// across subsequent auto-populate / merge-mode bin loads. Call
    /// <see cref="ClearAllIdentifierSources"/> to wipe the tags too.
    /// </summary>
    public bool RemoveIdentifier(byte did)
    {
        bool removed;
        lock (identifiersLock) removed = identifiers.Remove(did);
        if (removed) IdentifiersChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>
    /// Sets only the provenance tag for a DID without touching the bytes.
    /// Used by Replace-all bin loads to mark every well-known DID as Bin
    /// source even when the bin didn't surface a value for it.
    /// </summary>
    public void SetIdentifierSource(byte did, DidSource source)
    {
        lock (identifiersLock) identifierSources[did] = source;
        IdentifiersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Returns the recorded provenance for a DID, or <see cref="DidSource.Blank"/>
    /// if none has ever been set (the default for a fresh ECU).
    /// </summary>
    public DidSource GetIdentifierSource(byte did)
    {
        lock (identifiersLock)
            return identifierSources.TryGetValue(did, out var s) ? s : DidSource.Blank;
    }

    /// <summary>
    /// Drops every recorded provenance tag. Used by Replace-all bin loads
    /// to reset the source state before re-marking. Does NOT touch the
    /// identifier byte map - callers that want to wipe values too must
    /// loop <see cref="RemoveIdentifier"/> separately.
    /// </summary>
    public void ClearAllIdentifierSources()
    {
        lock (identifiersLock) identifierSources.Clear();
        IdentifiersChanged?.Invoke(this, EventArgs.Empty);
    }
}
