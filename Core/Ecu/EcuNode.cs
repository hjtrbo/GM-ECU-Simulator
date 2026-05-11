using System.Collections.Concurrent;
using Common.Glitch;
using Common.Protocol;
using Core.Bus;
using Core.Transport;

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

    /// <summary>Raised after a PID is added, removed, or the list is cleared.</summary>
    public event EventHandler? PidsChanged;

    public ConcurrentDictionary<byte, Dpid> Dpids { get; } = new();
    public IsoTpReassembler Reassembler { get; } = new();
    public TesterPresentState TesterPresent { get; } = new();

    // PID addresses are 32-bit (Pid.Address is uint), so the dynamic-PID
    // tracking set has to match. Static configured PIDs aren't in here.
    public HashSet<uint> DynamicallyDefinedPids { get; } = new();

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

    public void AddDpid(Dpid dpid) => Dpids[dpid.Id] = dpid;

    /// <summary>
    /// Notifies subscribers that the PID list has changed without adding/removing.
    /// Editor calls this after mutating a PID's properties so the live monitor
    /// refreshes its column data.
    /// </summary>
    public void RaisePidsChanged() => PidsChanged?.Invoke(this, EventArgs.Empty);
}
