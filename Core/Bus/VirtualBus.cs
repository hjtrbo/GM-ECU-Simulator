using System.Diagnostics;
using Common.PassThru;
using Common.Protocol;
using Core.Ecu;
using Core.Replay;
using Core.Scheduler;
using Core.Services;using Core.Transport;
using Core.Utilities;

namespace Core.Bus;

// The virtual GMLAN bus. Holds the configured ECU nodes, the periodic
// DPID scheduler, and the TesterPresent ticker. Routes inbound frames
// from any J2534 channel to the matching ECU by destination CAN ID.
//
// Mutating the ECU set (Add/Remove/Replace) is thread-safe; iterators
// take a lock-protected snapshot so the IPC dispatcher and the editor
// UI never collide.
public sealed class VirtualBus
{
    private readonly List<EcuNode> nodes = new();
    private readonly Lock nodesLock = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();

    public DpidScheduler Scheduler { get; }
    public TesterPresentTicker Ticker { get; }
    public IdleBusSupervisor IdleSupervisor { get; }
    public double NowMs => clock.Elapsed.TotalMilliseconds;

    // Bin-replay coordinator. Set by the composition root (App.OnStartup).
    // Null in tests that don't exercise the replay path; the dispatcher
    // and ticker check for null before calling MaybeStart / MaybeStop.
    public BinReplayCoordinator? Replay { get; set; }

    // Last time any host activity was observed (any incoming IPC request).
    // Updated by RequestDispatcher.Dispatch via NoteHostActivity. The
    // IdleBusSupervisor checks the gap to detect a stalled / disconnected host.
    private long lastHostActivityMs;
    public long LastHostActivityMs => Volatile.Read(ref lastHostActivityMs);
    public void NoteHostActivity() => Volatile.Write(ref lastHostActivityMs, (long)NowMs);

    /// <summary>
    /// Raised by the IdleBusSupervisor when the bus has been idle past the
    /// threshold and a full reset is being applied. Subscribers (typically
    /// IpcSessionState) use this to cancel periodic timers and any other
    /// per-session resources tied to the now-vanished host.
    /// </summary>
    public event Action? IdleReset;
    internal void RaiseIdleReset() => IdleReset?.Invoke();

    /// <summary>Raised after Add/Remove/Replace mutates the ECU set.</summary>
    public event EventHandler? NodesChanged;

    /// <summary>
    /// Frame-level traffic sink. Set to non-null to receive a one-line
    /// human-readable record from the simulator's perspective: Rx = frame
    /// received from the J2534 host; Tx = frame the simulator generated
    /// for the host (with "- HOST FILTERED" appended if the host's own
    /// channel filter blocked delivery). Null means no logging.
    /// </summary>
    public Action<string>? LogFrame { get; set; }

    /// <summary>
    /// Always-on diagnostic sink for control-plane events (periodic message
    /// register/unregister, channel lifecycle, anomalies). Unlike LogFrame
    /// this is NOT gated by the "Log frame traffic" checkbox — diagnostic
    /// events are low-volume and the user wants to see them whenever they're
    /// debugging. Null means no logging.
    /// </summary>
    public Action<string>? LogDiagnostic { get; set; }

    internal void LogTx(uint chId, ReadOnlySpan<byte> frame)
    {
        var sink = LogFrame;
        if (sink == null) return;
        if (frame.Length < CanFrame.IdBytes) return;
        sink($"[chan {chId}] Rx {FormatId(CanFrame.ReadId(frame))} {HexFormat.Bytes(CanFrame.Payload(frame))}");
    }

    internal void LogRx(uint chId, ReadOnlySpan<byte> frame)
    {
        var sink = LogFrame;
        if (sink == null) return;
        if (frame.Length < CanFrame.IdBytes) return;
        sink($"[chan {chId}] Tx {FormatId(CanFrame.ReadId(frame))} {HexFormat.Bytes(CanFrame.Payload(frame))}");
    }

    internal void LogRxFiltered(uint chId, ReadOnlySpan<byte> frame)
    {
        var sink = LogFrame;
        if (sink == null) return;
        if (frame.Length < CanFrame.IdBytes) return;
        sink($"[chan {chId}] Tx {FormatId(CanFrame.ReadId(frame))} {HexFormat.Bytes(CanFrame.Payload(frame))} - HOST FILTERED");
    }

    private static string FormatId(uint id)
        => id <= 0x7FF ? id.ToString("X3") : id.ToString("X8");

    public VirtualBus()
    {
        Scheduler = new DpidScheduler(this);
        Ticker = new TesterPresentTicker(this, Scheduler);
        IdleSupervisor = new IdleBusSupervisor(this, Scheduler);
    }

    /// <summary>Snapshot copy — safe to enumerate cross-thread.</summary>
    public IReadOnlyList<EcuNode> Nodes
    {
        get { lock (nodesLock) return nodes.ToArray(); }
    }

    public void AddNode(EcuNode node)
    {
        lock (nodesLock) nodes.Add(node);
        NodesChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveNode(EcuNode node)
    {
        bool removed;
        lock (nodesLock) removed = nodes.Remove(node);
        if (!removed) return false;
        Scheduler.Stop(node, Array.Empty<byte>());
        NodesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ReplaceNodes(IEnumerable<EcuNode> newNodes)
    {
        EcuNode[] toStop;
        lock (nodesLock)
        {
            toStop = nodes.ToArray();
            nodes.Clear();
            nodes.AddRange(newNodes);
        }
        foreach (var n in toStop) Scheduler.Stop(n, Array.Empty<byte>());
        NodesChanged?.Invoke(this, EventArgs.Empty);
    }

    public EcuNode? FindByRequestId(uint canId)
    {
        lock (nodesLock) return nodes.FirstOrDefault(n => n.PhysicalRequestCanId == canId);
    }

    public void DispatchHostTx(ReadOnlySpan<byte> frame, ChannelSession ch)
    {
        if (frame.Length < CanFrame.IdBytes + 1) return;

        LogTx(ch.Id, frame);

        uint canId = CanFrame.ReadId(frame);
        var data = CanFrame.Payload(frame);

        if (canId == GmlanCanId.AllNodesRequest)
        {
            DispatchFunctional(data, ch);
            return;
        }

        var node = FindByRequestId(canId);
        if (node == null)
        {
            LogFrame?.Invoke($"[bus] no ECU at {FormatId(canId)} -- frame dropped");
            return;
        }

        var assembled = node.Reassembler.Feed(data, (bs, st) =>
        {
            var fc = new byte[CanFrame.IdBytes + 3];
            CanFrame.WriteId(fc, node.UsdtResponseCanId);
            fc[CanFrame.IdBytes + 0] = (byte)PciType.FlowControl;
            fc[CanFrame.IdBytes + 1] = bs;
            fc[CanFrame.IdBytes + 2] = st;
            ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = fc });
        });

        if (assembled == null) return;
        DispatchUsdt(node, assembled, ch, isFunctional: false);
    }

    private void DispatchFunctional(ReadOnlySpan<byte> data, ChannelSession ch)
    {
        if (data.Length < 3) return;
        byte extAddr = data[0];
        byte pci = data[1];
        if ((pci >> 4) != 0) return;
        int len = pci & 0x0F;
        if (len < 1 || len > data.Length - 2) return;
        if (extAddr != GmlanCanId.AllNodesExtAddr) return;
        var payload = data.Slice(2, len);

        EcuNode[] snapshot;
        lock (nodesLock) snapshot = nodes.ToArray();
        foreach (var node in snapshot)
            DispatchUsdt(node, payload, ch, isFunctional: true);
    }

    private static void ActivateP3C(EcuNode node, ChannelSession ch)
    {
        node.LastEnhancedChannel = ch;
        node.TesterPresent.Activate();
    }

    private void DispatchUsdt(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch, bool isFunctional)
    {
        if (usdt.Length < 1) return;
        byte sid = usdt[0];
        // Bin-replay start trigger: first $22 / $AA after a host connects.
        // CAS-only inside MaybeStart; safe under concurrent dispatch on
        // multiple ECUs from different IPC pipe threads.
        if (sid == Service.ReadDataByParameterIdentifier
            || sid == Service.ReadDataByPacketIdentifier)
            Replay?.MaybeStart(NowMs);
        switch (sid)
        {
            case Service.ReadDataByParameterIdentifier:
                if (isFunctional) return;
                Service22Handler.Handle(node, usdt, ch, NowMs);
                break;
            case Service.DefinePidByAddress:
                if (isFunctional) return;
                if (Service2DHandler.Handle(node, usdt, ch))
                    ActivateP3C(node, ch);
                break;
            case Service.DynamicallyDefineMessage:
                if (isFunctional) return;
                if (Service2CHandler.Handle(node, usdt, ch))
                    ActivateP3C(node, ch);
                break;
            case Service.ReadDataByPacketIdentifier:
                if (isFunctional) return;
                if (ServiceAAHandler.Handle(node, usdt, ch, Scheduler))
                    ActivateP3C(node, ch);
                break;
            case Service.TesterPresent:
                Service3EHandler.Handle(node, usdt, ch, isFunctional);
                break;
            case Service.ReturnToNormalMode:
                if (isFunctional) { EcuExitLogic.Run(node, Scheduler, null); return; }
                Service20Handler.Handle(node, usdt, ch, Scheduler);
                break;
            case Service.InitiateDiagnosticOperation:
                if (isFunctional) return;
                if (Service10Handler.Handle(node, usdt, ch))
                    ActivateP3C(node, ch);
                break;
            default:
                break;
        }
    }
}
