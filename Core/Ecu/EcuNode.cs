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
