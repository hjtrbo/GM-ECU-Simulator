using Common.IsoTp;
using Common.PassThru;
using Core.Bus;
using Core.Utilities;

namespace Core.Transport;

/// <summary>
/// ISO 15765-2:2016 sender, per-EcuNode. Drives the
/// <see cref="IsoTpTxStateMachine"/> through the FF/FC/CF cascade and uses
/// <see cref="TimerOnDelay"/> for STmin pacing between consecutive frames
/// and N_Bs supervision while waiting for FlowControl.
///
/// Spec citations:
///   §9.2-9.3        SF vs FF/CF segmentation
///   §9.6.5.1 OVFLW  abort on FC.OVFLW
///   §9.6.5.1 WAIT   restart N_Bs and keep waiting
///   §9.6.5.2        reserved FlowStatus -> N_INVALID_FS
///   §9.6.5.6        dynamic BS/STmin honoured per FC
///   §9.8.2 N_Bs     N_TIMEOUT_Bs after the configured wait without an FC
///
/// Threading: a service handler kicks <see cref="EnqueueResponse"/> on the
/// IPC dispatcher thread; inbound FC frames also arrive on that thread.
/// STmin/N_Bs timers fire on the shared TimerScheduler high-priority
/// thread. <see cref="sync"/> protects state-machine access; I/O happens
/// outside the lock so the synchronous in-process cascade can re-enter
/// <see cref="OnFlowControl"/> without recursive-lock contention.
/// </summary>
public sealed class IsoTpFragmenter : IDisposable
{
    private readonly Lock sync = new();
    private readonly IsoTpTimingParameters timing;

    // Active TX state. All four are set together when EnqueueResponse starts a
    // multi-frame transmit, cleared together when it completes or aborts.
    private IsoTpTxStateMachine? activeTx;
    private ChannelSession? activeChannel;
    private uint activeResponseCanId;
    private TimerOnDelay? sTminTimer;
    private TimerOnDelay? nBsTimer;

    /// <summary>Last terminal N_Result from the most recent send. Useful for tests/diagnostics.</summary>
    public NResult? LastResult { get; private set; }

    public IsoTpFragmenter() : this(new IsoTpTimingParameters()) { }

    public IsoTpFragmenter(IsoTpTimingParameters timing)
    {
        this.timing = timing;
    }

    /// <summary>True when a multi-frame send is in progress (FF emitted, message not yet complete).</summary>
    public bool InProgress { get { lock (sync) return activeTx != null; } }

    /// <summary>
    /// Begin sending a USDT response. For a payload that fits in a SingleFrame
    /// (≤ 7 bytes with normal addressing), the SF is emitted immediately and
    /// the call returns - no FC handshake required. For larger payloads, the
    /// FF is emitted and the cascade is then driven by inbound FC frames
    /// (delivered via <see cref="OnFlowControl"/>) and the STmin timer.
    /// </summary>
    public void EnqueueResponse(ChannelSession ch, uint canId, ReadOnlySpan<byte> usdtPayload)
    {
        if (usdtPayload.Length == 0)
            throw new ArgumentException("Cannot send empty USDT payload (no SF_DL=0 in §9.6.2.1)");

        TxStartResult start;
        lock (sync)
        {
            // If a previous send is still in flight, cancel it. Defensive: the
            // spec doesn't allow concurrent sends on the same N_AI; service
            // handlers shouldn't ever overlap, but a misbehaving handler
            // shouldn't strand state.
            CancelActiveLocked();

            // dataFieldBytes = 8 for normal addressing on classical CAN.
            // Address-extension support comes in when EcuNode grows mixed
            // addressing - until then, every ECU uses the 8-byte budget.
            activeTx = new IsoTpTxStateMachine(8, timing);
            activeChannel = ch;
            activeResponseCanId = canId;
            LastResult = null;
            start = activeTx.Begin(usdtPayload);
        }

        EmitFrame(start.FrameToSend, ch, canId);

        if (start.Next == NextStep.Done)
        {
            // SingleFrame path: complete already.
            lock (sync) { LastResult = NResult.N_OK; ClearActiveLocked(); }
            return;
        }

        // FirstFrame path: arm N_Bs and wait for FC.
        ArmNbsTimer();
    }

    /// <summary>
    /// Cancels an in-flight transmit if its target channel matches
    /// <paramref name="ch"/>. Called from IpcSessionState.RemoveChannel so a
    /// host disconnect mid-multi-frame doesn't leave the fragmenter holding a
    /// reference to a disposed channel - which would later cause the N_Bs or
    /// STmin timer to fire EnqueueRx onto a torn-down RxQueue / IsoChannel.
    /// </summary>
    public void AbortIfActiveOn(ChannelSession ch)
    {
        lock (sync)
        {
            if (activeChannel != ch) return;
            // Latch as "general error" - the spec doesn't have a specific
            // N_Result for "channel went away mid-TX"; this is the closest fit
            // and is what an outer Dispose-style abort would conventionally use.
            LastResult = NResult.N_ERROR;
            ClearActiveLocked();
        }
    }

    /// <summary>
    /// Process an inbound FC frame routed from <see cref="VirtualBus.DispatchHostTx"/>.
    /// No-op if no transmit is in progress (stray FC).
    /// </summary>
    public void OnFlowControl(FlowStatus fs, byte bs, byte stMinRaw)
    {
        TxFcResult fcr;
        ChannelSession? ch;
        uint canId;
        lock (sync)
        {
            if (activeTx == null) return;       // stray FC, no TX awaits it
            CancelNbsTimerLocked();             // FC arrived in time
            fcr = activeTx.OnFlowControl(fs, bs, stMinRaw);
            ch = activeChannel;
            canId = activeResponseCanId;
        }
        DriveCascade(fcr, ch, canId);
    }

    /// <summary>
    /// Iterative cascade driver. Emits the supplied frame, then either arms a
    /// timer (STmin pacing or N_Bs wait), terminates (Done/Aborted), or loops
    /// to fetch the next CF immediately when STmin = 0.
    ///
    /// Re-entrancy: <see cref="EmitFrame"/> may trigger an inbound FC via the
    /// in-process bus cascade, which calls back into <see cref="OnFlowControl"/>
    /// while we're still inside DriveCascade. The lock-then-snapshot pattern
    /// keeps state mutations atomic; if the inner call clears activeTx, the
    /// outer iteration's next lock acquire sees null and returns gracefully.
    /// </summary>
    private void DriveCascade(TxFcResult initial, ChannelSession? ch, uint canId)
    {
        var current = initial;
        while (true)
        {
            if (current.FrameToSend != null && ch != null)
                EmitFrame(current.FrameToSend, ch, canId);

            // Latch terminal results so callers / tests can observe them.
            switch (current.Result)
            {
                case NResult.N_OK:
                    break;
                default:
                    lock (sync) { LastResult = current.Result; ClearActiveLocked(); }
                    return;
            }

            switch (current.Next)
            {
                case NextStep.WaitForSeparationTime:
                {
                    int stMinUs;
                    bool stillActive;
                    lock (sync)
                    {
                        stillActive = activeTx != null;
                        stMinUs = activeTx?.EffectiveStMinUs ?? 0;
                    }
                    if (!stillActive) return;

                    if (stMinUs <= 0)
                    {
                        // No pacing required; loop and emit the next CF inline.
                        lock (sync)
                        {
                            if (activeTx == null) return;
                            current = activeTx.OnSeparationTimeElapsed();
                        }
                        continue;
                    }

                    int stMinMs = (stMinUs + 999) / 1000;
                    ArmStminTimer(stMinMs);
                    return;
                }

                case NextStep.WaitForFlowControl:
                    ArmNbsTimer();
                    return;

                case NextStep.Done:
                    lock (sync) { LastResult ??= NResult.N_OK; ClearActiveLocked(); }
                    return;

                case NextStep.None:
                default:
                    return;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Timer-driven callbacks
    // -----------------------------------------------------------------------

    private void OnStminElapsed()
    {
        TxFcResult fcr;
        ChannelSession? ch;
        uint canId;
        lock (sync)
        {
            if (activeTx == null) return;       // cancelled while timer was pending
            CancelStminTimerLocked();
            fcr = activeTx.OnSeparationTimeElapsed();
            ch = activeChannel;
            canId = activeResponseCanId;
        }
        DriveCascade(fcr, ch, canId);
    }

    private void OnNbsElapsed()
    {
        // §9.8.2: N_Bs expired. Abort the transmission with N_TIMEOUT_Bs.
        lock (sync)
        {
            if (activeTx == null) return;       // cancelled while timer was pending
            CancelNbsTimerLocked();
            var r = activeTx.OnNbsTimeout();
            LastResult = r ?? NResult.N_TIMEOUT_Bs;
            ClearActiveLocked();
        }
    }

    // -----------------------------------------------------------------------
    // Timer management - all caller-must-hold-no-lock variants ARM, locked
    // variants CANCEL. Re-arming uses Stop+new because TimerOnDelay forbids
    // changing Preset while running.
    // -----------------------------------------------------------------------

    private void ArmStminTimer(int ms)
    {
        var t = new TimerOnDelay
        {
            Preset = Math.Max(1, ms),
            DebugInstanceName = "IsoTpFragmenter",
            DebugTimerName = "STmin",
        };
        t.OnTimingDone += (_, _) => OnStminElapsed();
        lock (sync) { CancelStminTimerLocked(); sTminTimer = t; }
        t.Start();
    }

    private void ArmNbsTimer()
    {
        int ms = timing.NBsMs;
        if (ms <= 0) return;        // 0 disables the timeout (best-effort)
        var t = new TimerOnDelay
        {
            Preset = ms,
            DebugInstanceName = "IsoTpFragmenter",
            DebugTimerName = "N_Bs",
        };
        t.OnTimingDone += (_, _) => OnNbsElapsed();
        lock (sync) { CancelNbsTimerLocked(); nBsTimer = t; }
        t.Start();
    }

    private void CancelStminTimerLocked()
    {
        sTminTimer?.Stop();
        sTminTimer = null;
    }

    private void CancelNbsTimerLocked()
    {
        nBsTimer?.Stop();
        nBsTimer = null;
    }

    private void CancelActiveLocked()
    {
        CancelStminTimerLocked();
        CancelNbsTimerLocked();
        activeTx = null;
        activeChannel = null;
        activeResponseCanId = 0;
    }

    private void ClearActiveLocked()
    {
        CancelStminTimerLocked();
        CancelNbsTimerLocked();
        activeTx = null;
        activeChannel = null;
        activeResponseCanId = 0;
    }

    // -----------------------------------------------------------------------
    // Wire emission
    // -----------------------------------------------------------------------

    private static void EmitFrame(byte[] frameDataField, ChannelSession ch, uint canId)
    {
        // Wrap the state-machine output (PCI + N_Data, no addressing prefix
        // because EcuNode uses normal addressing) into a bus-format frame.
        var canFrame = new byte[CanFrame.IdBytes + frameDataField.Length];
        CanFrame.WriteId(canFrame, canId);
        frameDataField.CopyTo(canFrame.AsSpan(CanFrame.IdBytes));
        ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = canFrame });
    }

    public void Dispose()
    {
        lock (sync) ClearActiveLocked();
    }
}
