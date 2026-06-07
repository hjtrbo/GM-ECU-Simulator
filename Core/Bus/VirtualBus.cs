using Common.PassThru;
using Common.Protocol;
using Core.Ecu;
using Core.Replay;
using Core.Scheduler;
using Core.Services;
using Core.Transport;
using Core.Utilities;
using System.Diagnostics;

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

    // Emits the ECUs' DBC-driven CAN broadcast messages via Broadcaster while a host session is open.
    // Driven by the composition root (RebuildAndStart on HostConnected, StopAll on HostDisconnected).
    public BroadcastScheduler BroadcastScheduler { get; }
    public double NowMs => clock.Elapsed.TotalMilliseconds;

    /// <summary>
    /// Bootloader-capture configuration. Writes happen unconditionally when
    /// <see cref="CaptureSettings.CaptureDirectory"/> is set; null disables
    /// disk side effects (unit tests construct VirtualBus directly and
    /// leave it null, WPF startup sets the LOCALAPPDATA path).
    /// </summary>
    public CaptureSettings Capture { get; } = new();

    // Bin-replay coordinator. Set by the composition root (App.OnStartup).
    // Null in tests that don't exercise the replay path; the dispatcher
    // and ticker check for null before calling MaybeStart / MaybeStop.
    public BinReplayCoordinator? Replay { get; set; }

    // Last time any host activity was observed (any incoming IPC request).
    // Updated by Shim.Ipc.RequestDispatcher.Dispatch via NoteHostActivity.
    // Currently informational - the IdleBusSupervisor that used to react to
    // gaps in this stream was stubbed 2026-05-15 (see IdleBusSupervisor.cs
    // header); the value is kept because tests assert on it and a future
    // diagnostic / metrics path may want it again.
    private long lastHostActivityMs;
    public long LastHostActivityMs => Volatile.Read(ref lastHostActivityMs);
    public void NoteHostActivity() => Volatile.Write(ref lastHostActivityMs, (long)NowMs);

    /// <summary>
    /// Raised on an explicit bus-wide idle reset. The time-based supervisor
    /// that used to fire this was stubbed 2026-05-15; nothing in the current
    /// build raises it on a schedule. Kept so subscribers
    /// (Shim.Ipc.IpcSessionState, MainWindow file-log lifecycle, BinReplay)
    /// stay wired up if a future force-idle path calls IdleBusSupervisor.DoReset.
    /// </summary>
    public event Action? IdleReset;
    internal void RaiseIdleReset() => IdleReset?.Invoke();

    /// <summary>
    /// Raised when the host calls PassThruOpen (graceful session start).
    /// Counterpart to <see cref="HostDisconnected"/>; subscribers open
    /// per-session resources here (e.g. a fresh log file).
    /// Public Raise so Shim.Ipc.RequestDispatcher can fire it from the
    /// IPC layer.
    /// </summary>
    public event Action? HostConnected;
    public void RaiseHostConnected() => HostConnected?.Invoke();

    /// <summary>
    /// Raised when the host's J2534 session ends. Two paths fire it:
    ///   - graceful: RequestDispatcher.Close on PassThruClose;
    ///   - pipe drop: NamedPipeServer.HandleClientAsync's finally block, on
    ///     any non-clean exit (USB unplug, host crash, host process killed).
    /// Subscribers finalise per-session resources here so each capture
    /// lands as one tidy file with its closing trailer. They must be
    /// idempotent because pipe drop can follow a graceful Close.
    /// </summary>
    public event Action? HostDisconnected;
    public void RaiseHostDisconnected() => HostDisconnected?.Invoke();

    /// <summary>Raised after Add/Remove/Replace mutates the ECU set.</summary>
    public event EventHandler? NodesChanged;

    /// <summary>
    /// Cross-channel raw-frame broadcast hook. Set by the Shim's
    /// IpcSessionState at construct time so any Core code (personas,
    /// schedulers) can push a UUDT frame at every open channel. Null
    /// while no IPC session is alive - callers must null-check.
    /// </summary>
    public IFrameBroadcaster? Broadcaster { get; set; }

    /// <summary>
    /// Frame-level traffic sink. Set to non-null to receive one record per
    /// frame in two formats simultaneously:
    ///   pretty - human-readable, space-delimited, for the on-screen textbox
    ///            e.g. "[chan 1] Rx 7E2 02 10 02  ; StartDiagnosticSession"
    ///   csv    - comma-separated for the log file; leading [timestamp],[CAN]
    ///            prefix is prepended by MainWindow before the disk write
    ///            e.g. "[chan 1],Rx,7E2 02 10 02,StartDiagnosticSession"
    /// Rx = frame received from the J2534 host; Tx = frame the simulator
    /// generated for the host ("- HOST FILTERED" appended when the host's own
    /// channel filter blocked delivery). The third argument is true when the
    /// frame carries a TesterPresent request ($3E) or its positive response
    /// ($7E) - the UI log pane can use this to suppress keepalive noise
    /// independently of the file-log capture. Null means no logging.
    /// </summary>
    public Action<string, string, bool>? LogFrame { get; set; }

    /// <summary>
    /// When true, every bus-frame log line is suffixed with a short
    /// human-readable tag produced by <see cref="Common.Protocol.UdsAnnotator"/>
    /// (e.g. "  ; SecurityAccess - RequestSeed"). Off by default - testers
    /// who only want raw hex pay no annotation cost on the hot path.
    /// </summary>
    public bool AnnotateFrames { get; set; }

    /// <summary>
    /// When true, long ISO-TP transfers in the bus log are condensed: the
    /// first <see cref="BulkTransferCollapser.HeadCfCount"/> consecutive
    /// frames pass through, the middle is replaced with a single marker
    /// line summarising how many frames were hidden, then the last
    /// <see cref="BulkTransferCollapser.TailCfCount"/> CFs come through.
    /// Transitions off→on do nothing for transfers already in progress;
    /// transitions on→off call <see cref="BulkTransferCollapser.Reset"/>
    /// so the next pass starts clean.
    /// </summary>
    public bool CollapseBulkTransfers
    {
        get => collapseBulkTransfers;
        set
        {
            if (collapseBulkTransfers == value) return;
            collapseBulkTransfers = value;
            if (!value) bulkCollapser.Reset();
        }
    }
    private bool collapseBulkTransfers;
    private readonly BulkTransferCollapser bulkCollapser = new();

    // When true, RequestDispatcher.StartPeriodic creates a timer that drives
    // $3E TesterPresent on the host's behalf for any host that registered the
    // frame via PassThruStartPeriodicMsg. Off makes the simulator accept the
    // registration but not tick - the host's P3C session lapses unless it
    // sends $3E some other way. Process-wide because it's a simulator-helper
    // policy, not an ECU trait; MainViewModel pushes the user's persisted
    // AppSettings choice in on startup and on every menu toggle.
    public bool AllowPeriodicTesterPresent { get; set; } = true;

    /// <summary>
    /// Diagnostic sink for events emitted from the Shim/ project: PassThru*
    /// IPC narration (with embedded multi-line tx[i]/rx[i] detail), pipe
    /// connect/disconnect, periodic-message register/unregister, idle-drain
    /// notices. Written to the file log with the [J2534] tag. Unlike
    /// LogFrame this is NOT gated by the "Log frame traffic" checkbox.
    /// Null means no logging.
    /// </summary>
    public Action<string>? LogJ2534 { get; set; }

    /// <summary>
    /// Diagnostic sink for simulator-internal events: service-handler
    /// decisions, security-module state transitions, DPID scheduler stalls,
    /// idle-supervisor exits, capture-writer output, app lifecycle.
    /// Written to the file log with the [SIM] tag. Null means no logging.
    /// </summary>
    public Action<string>? LogSim { get; set; }

    /// <summary>
    /// Higher-prominence sink for events the user should see at a glance -
    /// surfaced in the status bar at the bottom of the main window. Reserved
    /// for things that meaningfully change what the simulator is doing for
    /// a host (rejected connect attempts, etc.). Null means no surfacing.
    /// </summary>
    public Action<string>? OnStatusMessage { get; set; }

    // Two formats are emitted to the LogFrame sink per frame:
    //
    //   pretty  "[chan N] <Rx|Tx> <canId payload> [; annotation]"
    //           human-readable, rendered in the on-screen textbox as-is. The
    //           channel tag stays on the pretty line because a glance at the
    //           textbox during multi-channel debugging needs the disambiguator.
    //
    //   csv     "[<Rx|Tx>],<canId payload> [- HOST FILTERED],<annotation>"
    //           BusLogger prepends "[timestamp],[CAN]," before the disk write,
    //           producing the 5-column shape "[time],[CAN],[Rx],<canId>,<note>".
    //           The channel column was dropped per user request: in practice all
    //           real-world traces are single-channel and the constant "[chan 1],"
    //           prefix was wasted spreadsheet width. The annotation column is
    //           always present (empty string when off or annotator returns null)
    //           for stable column count.
    //
    // Collapse markers from BulkTransferCollapser follow the same two shapes:
    //   pretty  "[chan N] -- bulk transfer collapsed: N frames hidden --"
    //   csv     ",,-- bulk transfer collapsed: N frames hidden --"

    internal void LogTx(uint chId, ReadOnlySpan<byte> frame)
    {
        var sink = LogFrame;
        if (sink == null) return;
        if (frame.Length < CanFrame.IdBytes) return;
        var canId = CanFrame.ReadId(frame);
        var payload = CanFrame.Payload(frame);
        var (pretty, csv) = FormatFrame(chId, "Rx", canId, payload, hostFiltered: false);
        bool isTp = Common.Protocol.UdsAnnotator.IsTesterPresent(canId, payload);
        EmitFrame(chId, canId, payload, pretty, csv, isTp, sink);
    }

    internal void LogRx(uint chId, ReadOnlySpan<byte> frame)
    {
        var sink = LogFrame;
        if (sink == null) return;
        if (frame.Length < CanFrame.IdBytes) return;
        var canId = CanFrame.ReadId(frame);
        var payload = CanFrame.Payload(frame);
        var (pretty, csv) = FormatFrame(chId, "Tx", canId, payload, hostFiltered: false);
        bool isTp = Common.Protocol.UdsAnnotator.IsTesterPresent(canId, payload);
        EmitFrame(chId, canId, payload, pretty, csv, isTp, sink);
    }

    internal void LogRxFiltered(uint chId, ReadOnlySpan<byte> frame)
    {
        var sink = LogFrame;
        if (sink == null) return;
        if (frame.Length < CanFrame.IdBytes) return;
        var canId = CanFrame.ReadId(frame);
        var payload = CanFrame.Payload(frame);
        var (pretty, csv) = FormatFrame(chId, "Tx", canId, payload, hostFiltered: true);
        bool isTp = Common.Protocol.UdsAnnotator.IsTesterPresent(canId, payload);
        EmitFrame(chId, canId, payload, pretty, csv, isTp, sink);
    }

    // Width the byte field is padded to so annotation tags align in a column;
    // see the comment in FormatFrame for the derivation.
    private const int AnnotationColumn = 27;

    // Builds both the pretty (textbox) and csv (file) representations from
    // the same intermediate data so the annotator is invoked at most once.
    private (string pretty, string csv) FormatFrame(uint chId, string direction, uint canId, ReadOnlySpan<byte> payload, bool hostFiltered)
    {
        string bytes = $"{FormatId(canId)} {HexFormat.Bytes(payload)}";
        if (hostFiltered) bytes += " - HOST FILTERED";
        string? annotation = AnnotateFrames
            ? Common.Protocol.UdsAnnotator.Annotate(canId, payload)
            : null;
        string pretty;
        if (annotation != null)
        {
            // Pad the byte field to a fixed width so the "; <tag>" annotations
            // line up in a column across frames. AnnotationColumn covers an
            // 11-bit ID (3 hex) plus a full 8-byte CAN payload
            // ("7E0 07 23 00 01 00 C0 00 04" = 27 chars); wider frames (29-bit
            // IDs or the " - HOST FILTERED" suffix) just push their tag right.
            string field = bytes.Length < AnnotationColumn ? bytes.PadRight(AnnotationColumn) : bytes;
            pretty = $"[chan {chId}] {direction} {field}  ; {annotation}";
        }
        else
        {
            pretty = $"[chan {chId}] {direction} {bytes}";
        }
        string csv = $"[{direction}],{bytes},{annotation ?? ""}";
        return (pretty, csv);
    }

    private void EmitFrame(uint chId, uint canId, ReadOnlySpan<byte> payload, string pretty, string csv, bool isTesterPresent, Action<string, string, bool> sink)
    {
        if (collapseBulkTransfers)
            bulkCollapser.Process(chId, canId, payload, pretty, csv, isTesterPresent, sink);
        else
            sink(pretty, csv, isTesterPresent);
    }

    private static string FormatId(uint id)
        => id <= 0x7FF ? id.ToString("X3") : id.ToString("X8");

    public VirtualBus()
    {
        Scheduler          = new DpidScheduler(this);
        Ticker             = new TesterPresentTicker(this, Scheduler);
        IdleSupervisor     = new IdleBusSupervisor(this, Scheduler);
        BroadcastScheduler = new BroadcastScheduler(this);
    }

    /// <summary>Snapshot copy - safe to enumerate cross-thread.</summary>
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
        if (canId == GmlanCanId.Obd2FunctionalRequest)
        {
            // ISO 15765-4 functional broadcast. Normal addressing - no extAddr
            // byte to consume. Modern GM ECUs accept both this AND $101+$FE.
            DispatchObd2Functional(data, ch);
            return;
        }

        var node = FindByRequestId(canId);
        if (node == null)
        {
            var msg = $"[bus] no ECU at {FormatId(canId)} -- frame dropped";
            LogFrame?.Invoke(msg, msg, false);
            return;
        }

        // FlowControl frames inbound from the host are for OUR fragmenter (the
        // host is the receiver of an in-flight ECU response). Route to the
        // node's fragmenter instead of letting the reassembler discard them.
        // §9.6.5 - FC carries FS / BS / STmin in bytes 1..3 of the data field.
        if (data.Length >= 3 && (data[0] >> 4) == 0x3)
        {
            var fs = (Common.IsoTp.FlowStatus)(byte)(data[0] & 0x0F);
            byte bsByte = data[1];
            byte stMinByte = data[2];
            node.State.Fragmenter.OnFlowControl(fs, bsByte, stMinByte);
            return;
        }

        var assembled = node.State.Reassembler.Feed(data, (bs, st) =>
        {
            var fc = new byte[CanFrame.IdBytes + 3];
            CanFrame.WriteId(fc, node.UsdtResponseCanId);
            fc[CanFrame.IdBytes + 0] = (byte)PciType.FlowControl;
            fc[CanFrame.IdBytes + 1] = bs;
            fc[CanFrame.IdBytes + 2] = st;
            ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = fc });
        }, node.FlowControlBlockSize, fcSeparationTime: 0);

        if (assembled == null) return;
        // Tag physical requests with the stack the request CAN ID implies.
        // Real E38/E67 silicon routes each CAN ID to exactly one dispatcher;
        // future per-handler stack gates use this to NRC services that
        // aren't exposed on the caller's stack.
        var stack = DiagnosticStackClassifier.StackForCanId(canId);
        DispatchUsdt(node, assembled, ch, isFunctional: false, stack);
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

        // $101 + $FE extAddr is GMLAN's AllNodes functional broadcast - on real
        // E38/E67 silicon this routes through the OBD-II/UDS dispatcher (the
        // RE survey of CAN ID $101 confirmed it). Tag accordingly.
        EcuNode[] snapshot;
        lock (nodesLock) snapshot = nodes.ToArray();
        foreach (var node in snapshot)
            DispatchUsdt(node, payload, ch, isFunctional: true, DiagnosticStack.Uds);
    }

    /// <summary>
    /// ISO 15765-4 OBD-II functional broadcast at CAN ID $7DF. Normal addressing,
    /// so data[0] is the PCI byte (no extAddr byte to consume). Only Single
    /// Frame is meaningful for functional broadcast - multi-frame to many
    /// receivers would need per-receiver FlowControl, which the standard
    /// doesn't define for $7DF. Dispatches the SF payload to every ECU with
    /// isFunctional=true, so existing per-service gates (e.g. Service3EHandler
    /// accepting both physical and functional, Service22Handler returning
    /// early on functional, etc.) behave correctly.
    /// </summary>
    private void DispatchObd2Functional(ReadOnlySpan<byte> data, ChannelSession ch)
    {
        if (data.Length < 2) return;
        byte pci = data[0];
        if ((pci >> 4) != 0) return;                           // SF only
        int len = pci & 0x0F;
        if (len < 1 || len > data.Length - 1) return;
        var payload = data.Slice(1, len);

        EcuNode[] snapshot;
        lock (nodesLock) snapshot = nodes.ToArray();
        foreach (var node in snapshot)
            DispatchUsdt(node, payload, ch, isFunctional: true, DiagnosticStack.Uds);
    }

    private void DispatchUsdt(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch, bool isFunctional, DiagnosticStack stack)
    {
        if (usdt.Length < 1) return;
        byte sid = usdt[0];
        // Bin-replay start trigger: first $22 / $AA after a host connects.
        // CAS-only inside MaybeStart; safe under concurrent dispatch on
        // multiple ECUs from different IPC pipe threads.
        if (sid == Service.ReadDataByParameterIdentifier
            || sid == Service.ReadDataByPacketIdentifier)
            Replay?.MaybeStart(NowMs);

        // Exception isolation: a buggy waveform / security algorithm /
        // value-codec must not crash the bus thread or leave the channel
        // unusable. Any throw inside a handler is logged and translated to a
        // generic NRC $22 (CNCRSE - "ECU not in a state to perform this
        // service"). Physical requests get the NRC; functional broadcasts stay
        // silent on error per §6.x convention. The inner try guards against
        // the fragmenter ALSO throwing (e.g. if state is corrupted), so the
        // catch can never raise a second-order exception.
        try
        {
            // RAM-read fallback (per-ECU opt-in): a $23 ReadMemoryByAddress
            // targeting an address beyond the loaded flash bin (RAM) gets a
            // positive zero-filled reply instead of NRC $31 RequestOutOfRange.
            // Runs before persona dispatch so it applies to every persona;
            // in-bin reads fall through to the persona's own handler.
            if (sid == 0x23 && node.RamReadReturnsZeros
                && TryAnswerRamReadWithZeros(node, usdt, ch))
            {
                return;
            }

            // Delegate to the ECU's currently-active persona. Default is
            // Gmw3110Persona; a successful $36 sub $80 DownloadAndExecute
            // swaps it to UdsKernelPersona. A persona returning false means
            // "I don't recognise this SID" - the spec-correct reply for a
            // physical request is NRC $11 ServiceNotSupported; functional
            // broadcasts stay silent.
            if (!node.Persona.Dispatch(node, usdt, ch, isFunctional, sid, NowMs, Scheduler, stack)
                && !isFunctional)
            {
                ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.ServiceNotSupported);
            }
        }
        catch (Exception ex)
        {
            LogSim?.Invoke(
                $"[bus] dispatch error on ECU '{node.Name}' SID 0x{sid:X2}: {ex.GetType().Name}: {ex.Message}");
            if (!isFunctional)
            {
                try
                {
                    node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                        new byte[] { Service.NegativeResponse, sid, Nrc.ConditionsNotCorrectOrSequenceError });
                }
                catch (Exception ex2)
                {
                    LogSim?.Invoke(
                        $"[bus] failed to send fallback NRC for SID 0x{sid:X2}: {ex2.GetType().Name}: {ex2.Message}");
                }
            }
        }
    }

    // Answer a $23 ReadMemoryByAddress targeting RAM with a positive $63 reply
    // of <len> zero bytes. Returns true when handled, false when the request
    // isn't the recognised 7-byte ReadMemoryByAddress shape or the address
    // range lies wholly inside the loaded flash bin (in which case the persona
    // serves the real bytes). Request layout (the Ford UDS $23 wire format):
    //   23 <4-byte BE address> <2-byte BE length>   (7 bytes total)
    // "RAM" = any byte of the requested range lies at or past the bin length;
    // with no bin loaded the bin length is 0, so every address is RAM.
    private static bool TryAnswerRamReadWithZeros(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        if (usdt.Length != 7) return false;
        uint addr = (uint)((usdt[1] << 24) | (usdt[2] << 16) | (usdt[3] << 8) | usdt[4]);
        ushort len = (ushort)((usdt[5] << 8) | usdt[6]);
        if (len == 0) return false;

        uint binLength = (uint)Core.Ecu.Personas.FordUdsPersona.FlashBinSize;
        // Wholly inside the bin -> not RAM; let the persona serve real bytes.
        // ulong guards against addr+len wrapping for addresses near uint.MaxValue.
        if ((ulong)addr + len <= binLength) return false;

        var reply = new byte[1 + len];
        reply[0] = 0x63;                       // 0x23 | 0x40 positive response
        // reply[1..] stays zero-initialised - the zero-filled RAM payload.
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, reply);
        return true;
    }
}
