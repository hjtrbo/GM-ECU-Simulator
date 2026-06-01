using Common.Glitch;
using Common.Protocol;
using Common.Signals;
using Core.Ecu.Personas;
using Core.Security;
using System.Text.Json;

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

    // Set by ArchivePrimer.BuildEcuNode. Primed ECUs are live on the bus
    // during the session but are not written to ecu_config.json - they are
    // reconstructed at startup from the persisted PrimeArchivePath.
    public bool IsPrimed { get; set; }

    // ISO 15765-2 Flow Control BS byte emitted by this ECU's reassembler in
    // response to an inbound First Frame. The FC tail (after the 0x30 CTS
    // PCI byte) is `BS STmin`; STmin is hard-coded to 0 (no inter-frame
    // delay), which is the most permissive behaviour and what every host
    // we've tested against accepts.
    //
    // Override BS per ECU to mimic real silicon: e.g. the 6Speed.T43 tester
    // checks the FC bytes for the substring "01", so a TCM configured to
    // emit BS=1 (FC = 30 01 00) makes that pattern match and lets the
    // kernel-upload flow proceed.
    public byte FlowControlBlockSize { get; set; }

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
    /// GMW3110 8-bit diagnostic address. Returned in the $1A $B0 (Read ECU
    /// Diagnostic Address) response as the canonical "5A B0 &lt;diag_addr&gt;" reply,
    /// which is how testers and DPS rebuild their bus mapping matrix. Typically
    /// equals the low byte of <see cref="PhysicalRequestCanId"/>, e.g. $11.
    /// </summary>
    public byte DiagnosticAddress { get; set; }

    // Per-ECU glitch-injection configuration. The data model exists so the
    // UI/persistence layers can edit and round-trip these settings; the actual
    // injection logic in Core/Services is NOT yet implemented.
    public GlitchConfig Glitch { get; set; } = GlitchConfig.CreateDefault();

    // Three mode-keyed stores. Each PidMode owns its own dictionary keyed by the slice of Pid.Address the wire uses
    // for lookup, so a $1A request for DID $0C and a $22 request for 0x000C never reach into each other's namespace.
    // A single lock guards all three for simple atomic Add/Remove/Relocate. (Mode $01 is no longer a store - it is
    // the built-in J1979 projection over the signal layer; see Mode1Supported / Service01Handler.)
    //
    // Keys:
    //   mode1APids  key = (byte)(Pid.Address & 0xFF)        GMW3110 DID
    //   mode22Pids  key = (ushort)(Pid.Address & 0xFFFF)    $22 wire PID id
    //   mode2DPids  key = Pid.Address                       full 32-bit RAM addr
    //
    // $22 wire lookup for Mode2D goes through GetPidByWireId, which derives
    // the alias 0xF000 | (addr & 0x0FFF) from each Mode2D entry's address.
    private readonly Dictionary<byte,   Pid> mode1APids = new();
    private readonly Dictionary<ushort, Pid> mode22Pids = new();
    private readonly Dictionary<uint,   Pid> mode2DPids = new();
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

    // Signal-layer model for the redesigned diagnostic modes, added alongside the legacy per-mode Pid stores so the
    // modes can migrate one at a time ($01 reads from here first). The universal physics live in EngineModel /
    // J1979Catalogue (shared by every ECU); only the per-ECU pieces sit on the node.

    // Per-ECU live engine model: the scenario-driven signals every live projection reads from.
    public EngineModel EngineModel { get; } = new();

    // Per-ECU non-analog state behind the OBD-II status PIDs (MIL, DTC count, O2 layout, conformance, fuel type).
    public DiscreteState DiscreteState { get; } = new();

    // The J1979 Mode $01 PIDs this ECU advertises. The $00/$20/... support bitmasks are computed from exactly this
    // subset, so the advertised map can never claim a PID the ECU won't answer. A per-ECU copy of the catalogue
    // default, so toggling one ECU's set never mutates the shared default or another ECU's. The editor's $01 rows
    // flip membership via SetMode1Supported.
    private HashSet<byte> mode1Supported = new(J1979Catalogue.DefaultSupported);
    public IReadOnlySet<byte> Mode1Supported
    {
        get => mode1Supported;
        set => mode1Supported = new HashSet<byte>(value);
    }

    // Enable or disable a single J1979 PID in this ECU's advertised $01 subset (drives the $00/$20 support bitmask).
    public void SetMode1Supported(byte pid, bool supported)
    {
        if (supported) mode1Supported.Add(pid); else mode1Supported.Remove(pid);
    }

    // For ECUs using the ford-capture persona, the path of the flash bin
    // loaded into FordCapturePersona at config-apply time. Stored here
    // purely so the save path (ConfigStore.EcuDtoFrom) can round-trip the
    // field back to JSON without losing it through a UI save. The persona
    // itself owns the byte array (static singleton); this is a bookkeeping
    // mirror, not a second copy of the data.
    public string? FlashBinPath { get; set; }

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

    /// <summary>
    /// Snapshot of every PID across every mode-keyed store, ordered by mode
    /// then by key for deterministic enumeration. Returns an array copy under
    /// the lock so callers can iterate cross-thread without holding it.
    /// Replaces the legacy <c>Pids</c> + <c>Mode1Pids</c> split.
    /// </summary>
    public IEnumerable<Pid> AllPids
    {
        get
        {
            lock (pidsLock)
            {
                var arr = new Pid[mode22Pids.Count + mode2DPids.Count + mode1APids.Count];
                int i = 0;
                foreach (var kv in mode22Pids.OrderBy(kv => kv.Key))  arr[i++] = kv.Value;
                foreach (var kv in mode2DPids.OrderBy(kv => kv.Key))  arr[i++] = kv.Value;
                foreach (var kv in mode1APids.OrderBy(kv => kv.Key))  arr[i++] = kv.Value;
                return arr;
            }
        }
    }

    /// <summary>Look up by full <see cref="Pid.Address"/> across every store.
    /// For Mode22/Mode1A/Mode1 the address is the wire id; for Mode2D it is the
    /// 32-bit memory address. First match wins in Mode22 -> Mode2D -> Mode1A ->
    /// Mode1 order.</summary>
    public Pid? GetPid(uint address)
    {
        lock (pidsLock)
        {
            if (address <= 0xFFFF && mode22Pids.TryGetValue((ushort)address, out var m22)) return m22;
            if (mode2DPids.TryGetValue(address, out var m2D))                                return m2D;
            if (address <= 0xFF   && mode1APids.TryGetValue((byte)address, out var m1A))    return m1A;
            return null;
        }
    }

    /// <summary>Wire-side $22 lookup. Mode22 hits the dict directly; Mode2D
    /// matches via the derived alias <c>0xF000 | (Address &amp; 0x0FFF)</c>.
    /// Mode1A / Mode1 rows are unreachable here by design - $22 has its own
    /// 2-byte PID id namespace disjoint from $1A's 1-byte DID space and $01's
    /// 1-byte PID space.</summary>
    public Pid? GetPidByWireId(ushort wireId)
    {
        lock (pidsLock)
        {
            if (mode22Pids.TryGetValue(wireId, out var m22)) return m22;
            // Mode2D alias scan. The dict is typically small (single-digit
            // entries even on heavily-used $2D sessions), so a linear walk
            // is cheaper than maintaining a parallel alias->Pid map.
            foreach (var (addr, pid) in mode2DPids)
                if ((ushort)(0xF000 | (addr & 0x0FFF)) == wireId) return pid;
            return null;
        }
    }

    /// <summary>$1A handler hook. Returns the Mode1A row for the given DID,
    /// or null - the caller falls back to <c>GetIdentifier</c> for bin/archive-
    /// seeded values that weren't overridden in the editor grid.</summary>
    public Pid? GetMode1APid(byte did)
    {
        lock (pidsLock) return mode1APids.TryGetValue(did, out var p) ? p : null;
    }

    /// <summary>Insert or replace a PID. Routes to the per-mode store based on
    /// <see cref="Pid.Mode"/>; replaces any existing entry with the same key in
    /// that store. Always raises <see cref="PidsChanged"/>.</summary>
    public void AddPid(Pid pid)
    {
        // Bind every added PID to this ECU's engine model so a signal-backed PID can resolve live values; harmless
        // for non-signal PIDs (the engine reference is only consulted when Pid.Signal is set).
        pid.AttachEngine(EngineModel);
        lock (pidsLock)
        {
            switch (pid.Mode)
            {
                case PidMode.Mode1A: mode1APids[(byte)(pid.Address & 0xFF)]    = pid; break;
                case PidMode.Mode22: mode22Pids[(ushort)(pid.Address & 0xFFFF)] = pid; break;
                case PidMode.Mode2D: mode2DPids[pid.Address]                    = pid; break;
            }
        }
        PidsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Remove a PID. Targets the store that owns <see cref="Pid.Mode"/>;
    /// uses <see cref="Pid.Address"/>-derived key for the lookup. Returns true
    /// when an entry was removed.</summary>
    public bool RemovePid(Pid pid)
    {
        bool removed;
        lock (pidsLock)
        {
            removed = pid.Mode switch
            {
                PidMode.Mode1A  => mode1APids.Remove((byte)(pid.Address & 0xFF)),
                PidMode.Mode22  => mode22Pids.Remove((ushort)(pid.Address & 0xFFFF)),
                PidMode.Mode2D  => mode2DPids.Remove(pid.Address),
                _               => false,
            };
        }
        if (removed)
            PidsChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>Remove every PID whose <see cref="Pid.Address"/> matches across
    /// every store. The caller doesn't know which mode owns the address - used
    /// by <c>EcuExitLogic</c> to clean up $2D-defined dynamic PIDs (registered
    /// as Mode22 with Address = pidId) at session end.</summary>
    public bool RemovePidByAddress(uint address)
    {
        bool removed = false;
        lock (pidsLock)
        {
            if (address <= 0xFFFF && mode22Pids.Remove((ushort)address)) removed = true;
            if (mode2DPids.Remove(address))                              removed = true;
            if (address <= 0xFF   && mode1APids.Remove((byte)address))   removed = true;
        }
        if (removed)
            PidsChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>Move <paramref name="pid"/> between mode stores in one atomic
    /// step. Called by the editor's mode-flip handler: the underlying Pid
    /// instance stays the same (the editor's ObservableCollection doesn't
    /// churn), only its storage location moves. Removes from the store keyed
    /// by <paramref name="oldMode"/>, inserts into the store keyed by the
    /// pid's CURRENT mode (which the caller has already updated).</summary>
    public void RelocatePidMode(Pid pid, PidMode oldMode)
    {
        if (oldMode == pid.Mode) return;
        lock (pidsLock)
        {
            switch (oldMode)
            {
                case PidMode.Mode1A: mode1APids.Remove((byte)(pid.Address & 0xFF));    break;
                case PidMode.Mode22: mode22Pids.Remove((ushort)(pid.Address & 0xFFFF)); break;
                case PidMode.Mode2D: mode2DPids.Remove(pid.Address);                    break;
            }
            switch (pid.Mode)
            {
                case PidMode.Mode1A: mode1APids[(byte)(pid.Address & 0xFF)]    = pid; break;
                case PidMode.Mode22: mode22Pids[(ushort)(pid.Address & 0xFFFF)] = pid; break;
                case PidMode.Mode2D: mode2DPids[pid.Address]                    = pid; break;
            }
        }
        PidsChanged?.Invoke(this, EventArgs.Empty);
    }

    // Re-key a PID after its Address changed in place. The per-mode stores are keyed by Address, so editing only
    // Pid.Address (the editor's Address column) would leave the entry under its old key and every GetPid /
    // GetPidByWireId lookup by the new address would miss. Callers pass the prior address so we can move the entry
    // within the PID's current mode store. No-op when the address is unchanged.
    public void RekeyPidAddress(Pid pid, uint oldAddress)
    {
        if (oldAddress == pid.Address) return;
        lock (pidsLock)
        {
            switch (pid.Mode)
            {
                case PidMode.Mode1A:
                    mode1APids.Remove((byte)(oldAddress & 0xFF));
                    mode1APids[(byte)(pid.Address & 0xFF)] = pid;
                    break;
                case PidMode.Mode22:
                    mode22Pids.Remove((ushort)(oldAddress & 0xFFFF));
                    mode22Pids[(ushort)(pid.Address & 0xFFFF)] = pid;
                    break;
                case PidMode.Mode2D:
                    mode2DPids.Remove(oldAddress);
                    mode2DPids[pid.Address] = pid;
                    break;
            }
        }
        PidsChanged?.Invoke(this, EventArgs.Empty);
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
