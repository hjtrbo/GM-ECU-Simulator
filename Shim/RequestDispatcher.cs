using Common.IsoTp;
using Common.PassThru;
using Common.Wire;
using Core.Bus;
using Core.Utilities;
using Shim.IsoTp;

namespace Shim.Ipc;

// Dispatches inbound IPC frames to per-message-type handlers and returns the
// payload to send back. Pure CPU work - no blocking I/O. The pipe server
// owns the actual stream and serialization.
public sealed class RequestDispatcher
{
    // Upper bound on host-controlled message-count fields (ReadMsgs / WriteMsgs).
    // A real J2534 host never asks for more than a handful at a time; clamping
    // here prevents a u32 cast-to-int negative from allocating a multi-GB List.
    private const int MaxMsgsPerCall = 4096;

    private readonly IpcSessionState state;

    public RequestDispatcher(IpcSessionState state) { this.state = state; }

    public (byte responseType, byte[] payload) Dispatch(byte requestType, ReadOnlySpan<byte> payload)
    {
        // Touch the activity timestamp. The IdleBusSupervisor that consumed
        // this is stubbed (see IdleBusSupervisor.cs header), so the value is
        // informational now - tests still read it and a future metrics /
        // diagnostic path may put it back to use. Done at the IPC entry
        // rather than per-handler so every kind of request counts (including
        // the spammy ReadMsgs polls that we deliberately don't log).
        state.Bus.NoteHostActivity();

        LogIncoming(requestType, payload);
        return requestType switch
        {
            IpcMessageTypes.OpenRequest          => Open(payload),
            IpcMessageTypes.CloseRequest         => Close(payload),
            IpcMessageTypes.ConnectRequest       => Connect(payload),
            IpcMessageTypes.DisconnectRequest    => Disconnect(payload),
            IpcMessageTypes.ReadVersionRequest   => ReadVersion(payload),
            IpcMessageTypes.GetLastErrorRequest  => GetLastError(payload),
            IpcMessageTypes.IoctlRequest         => Ioctl(payload),
            IpcMessageTypes.ReadMsgsRequest      => ReadMsgs(payload),
            IpcMessageTypes.WriteMsgsRequest     => WriteMsgs(payload),
            IpcMessageTypes.StartFilterRequest   => StartFilter(payload),
            IpcMessageTypes.StopFilterRequest    => StopFilter(payload),
            IpcMessageTypes.StartPeriodicRequest => StartPeriodic(payload),
            IpcMessageTypes.StopPeriodicRequest  => StopPeriodic(payload),
            IpcMessageTypes.SetVoltageRequest    => SetVoltage(payload),
            IpcMessageTypes.CanaryRequest        => Canary(payload),
            _ => Failed(requestType, ResultCode.ERR_NOT_SUPPORTED),
        };
    }

    // Log every incoming J2534 call to the diagnostic sink (right pane in
    // the WPF UI). Skips the high-frequency polling calls (ReadMsgs,
    // GetLastError) and the internal connect-time canary handshake.
    private void LogIncoming(byte requestType, ReadOnlySpan<byte> payload)
    {
        var diag = state.Bus.LogDiagnostic;
        if (diag == null) return;
        if (requestType == IpcMessageTypes.ReadMsgsRequest) return;     // host-poll: every ~10–100ms
        if (requestType == IpcMessageTypes.GetLastErrorRequest) return; // follows almost every call
        if (requestType == IpcMessageTypes.CanaryRequest) return;       // internal startup handshake

        string desc;
        try
        {
            var r = new IpcReader(payload);
            switch (requestType)
            {
                case IpcMessageTypes.OpenRequest:
                    desc = "PassThruOpen";
                    break;
                case IpcMessageTypes.CloseRequest:
                    desc = $"PassThruClose dev={r.ReadU32()}";
                    break;
                case IpcMessageTypes.ConnectRequest:
                {
                    uint dev = r.ReadU32();
                    var proto = (ProtocolID)r.ReadU32();
                    uint flags = r.ReadU32();
                    uint baud = r.ReadU32();
                    desc = $"PassThruConnect dev={dev} proto={proto} flags=0x{flags:X} baud={baud}";
                    break;
                }
                case IpcMessageTypes.DisconnectRequest:
                    desc = $"PassThruDisconnect chan={r.ReadU32()}";
                    break;
                case IpcMessageTypes.WriteMsgsRequest:
                {
                    uint chan = r.ReadU32();
                    uint num = r.ReadU32();
                    uint timeout = r.ReadU32();
                    desc = $"PassThruWriteMsgs chan={chan} count={num} timeout={timeout}ms";
                    break;
                }
                case IpcMessageTypes.StartFilterRequest:
                {
                    uint chan = r.ReadU32();
                    var ftype = (FilterType)r.ReadU32();
                    desc = $"PassThruStartMsgFilter chan={chan} type={ftype}";
                    break;
                }
                case IpcMessageTypes.StopFilterRequest:
                {
                    uint chan = r.ReadU32();
                    uint fid = r.ReadU32();
                    desc = $"PassThruStopMsgFilter chan={chan} filter={fid}";
                    break;
                }
                case IpcMessageTypes.StartPeriodicRequest:
                {
                    uint chan = r.ReadU32();
                    uint interval = r.ReadU32();
                    desc = $"PassThruStartPeriodicMsg chan={chan} interval={interval}ms";
                    break;
                }
                case IpcMessageTypes.StopPeriodicRequest:
                {
                    uint chan = r.ReadU32();
                    uint id = r.ReadU32();
                    desc = $"PassThruStopPeriodicMsg chan={chan} id={id}";
                    break;
                }
                case IpcMessageTypes.IoctlRequest:
                {
                    uint chan = r.ReadU32();
                    uint id = r.ReadU32();
                    desc = $"PassThruIoctl chan={chan} id=0x{id:X2} ({IoctlName(id)})";
                    break;
                }
                case IpcMessageTypes.ReadVersionRequest:
                    desc = "PassThruReadVersion";
                    break;
                case IpcMessageTypes.SetVoltageRequest:
                    desc = "PassThruSetProgrammingVoltage";
                    break;
                default:
                    desc = $"<unknown 0x{requestType:X2}>";
                    break;
            }
        }
        catch
        {
            desc = $"<malformed 0x{requestType:X2}>";
        }
        diag(desc);
    }

    private static string IoctlName(uint id) => id switch
    {
        0x01 => "GET_CONFIG",
        0x02 => "SET_CONFIG",
        0x03 => "READ_VBATT",
        0x04 => "FIVE_BAUD_INIT",
        0x05 => "FAST_INIT",
        0x07 => "CLEAR_TX_BUFFER",
        0x08 => "CLEAR_RX_BUFFER",
        0x09 => "CLEAR_PERIODIC_MSGS",
        0x0A => "CLEAR_MSG_FILTERS",
        0x0B => "CLEAR_FUNCT_MSG_LOOKUP_TABLE",
        0x0C => "ADD_TO_FUNCT_MSG_LOOKUP_TABLE",
        0x0D => "DELETE_FROM_FUNCT_MSG_LOOKUP_TABLE",
        0x0E => "READ_PROG_VOLTAGE",
        _    => "?",
    };

    // ---------------- Version / canary ----------------

    private (byte, byte[]) ReadVersion(ReadOnlySpan<byte> payload)
    {
        var w = new IpcWriter();
        w.WriteU32((uint)ResultCode.STATUS_NOERROR);
        w.WriteStringU16Length("1.0.0");
        w.WriteStringU16Length("1.0.0");
        w.WriteStringU16Length("04.04");
        return (IpcMessageTypes.ReadVersionResponse, w.ToArray());
    }

    // Reserved canary endpoint - echoes any payload back. The shim doesn't
    // currently send these; if a future handshake needs to verify wire-format
    // bytes survive the round trip, this is the path.
    private (byte, byte[]) Canary(ReadOnlySpan<byte> payload)
        => (IpcMessageTypes.CanaryResponse, payload.ToArray());

    // ---------------- Device / channel lifecycle ----------------

    private (byte, byte[]) Open(ReadOnlySpan<byte> payload)
    {
        // Notify session-scoped subscribers BEFORE the device id is allocated
        // so any per-session resources (file log, etc.) are primed by the
        // time the host issues its first follow-up call. Raising the event
        // is best-effort - a subscriber throw must not turn a successful
        // PassThruOpen into ERR_FAILED.
        try { state.Bus.RaiseHostConnected(); }
        catch (Exception ex)
        {
            state.Bus.LogDiagnostic?.Invoke($"[host-connected] subscriber threw: {ex.Message}");
        }
        var w = new IpcWriter();
        w.WriteU32((uint)ResultCode.STATUS_NOERROR);
        w.WriteU32(state.AllocateDeviceId());
        return (IpcMessageTypes.OpenResponse, w.ToArray());
    }

    private (byte, byte[]) Close(ReadOnlySpan<byte> payload)
    {
        // End-of-session hook. Subscribers (e.g. FileLogSink) finalise their
        // per-session resources here so each capture lands with a trailer.
        // Same throw-isolation rationale as Open - we return STATUS_NOERROR
        // regardless of what a misbehaving subscriber does.
        try { state.Bus.RaiseHostDisconnected(); }
        catch (Exception ex)
        {
            state.Bus.LogDiagnostic?.Invoke($"[host-disconnected] subscriber threw: {ex.Message}");
        }
        state.Bus.OnStatusMessage?.Invoke("J2534 host disconnected");
        return (IpcMessageTypes.CloseResponse, OkResultPayload());
    }

    // Request payload: u32 deviceId, u32 protocolId, u32 flags, u32 baud
    private (byte, byte[]) Connect(ReadOnlySpan<byte> payload)
    {
        var r = new IpcReader(payload);
        if (r.Remaining < 16) return ProtocolFail(IpcMessageTypes.ConnectResponse, ResultCode.ERR_INVALID_MSG);
        _ = r.ReadU32();                                  // deviceId (unused for now)
        var proto = (ProtocolID)r.ReadU32();
        var flags = r.ReadU32();                          // CAN_29BIT_ID etc - see ChannelSession.ConnectFlags
        var baud = r.ReadU32();

        // Accept CAN (raw frame forwarding) and ISO15765 (we run the ISO 15765-2
        // transport layer in this shim - segmentation, FC handshake, reassembly).
        // Other protocols (J1850, ISO9141, KWP2000) are not implemented.
        if (proto != ProtocolID.CAN && proto != ProtocolID.ISO15765)
        {
            state.Bus.LogDiagnostic?.Invoke(
                $"[connect] rejected: protocol {proto} not supported - this shim handles CAN and ISO15765");
            state.Bus.OnStatusMessage?.Invoke(
                $"Rejected J2534 connect: {proto} not supported");
            return ProtocolFail(IpcMessageTypes.ConnectResponse, ResultCode.ERR_INVALID_PROTOCOL_ID);
        }

        var ch = state.AllocateChannel(proto, baud, flags);

        // For ISO15765 channels, attach a per-channel TP context that drives
        // segmentation/reassembly. The BusEgress lambda dispatches outbound
        // CAN frames produced mid-cascade (FCs from our RX side, CFs from our
        // TX side) back through the bus.
        if (proto == ProtocolID.ISO15765)
        {
            var iso = new Iso15765Channel(new IsoTpTimingParameters());
            iso.BusEgress = frame => state.Bus.DispatchHostTx(frame, ch);
            ch.IsoChannel = iso;
            ch.IsoChannelInbound = (canId, frame) => iso.OnInboundCanFrame(canId, frame.AsSpan(4));
        }

        state.Bus.OnStatusMessage?.Invoke(
            $"J2534 host connected - channel {ch.Id}, {proto} @ {baud} baud");
        var w = new IpcWriter();
        w.WriteU32((uint)ResultCode.STATUS_NOERROR);
        w.WriteU32(ch.Id);
        return (IpcMessageTypes.ConnectResponse, w.ToArray());
    }

    private (byte, byte[]) Disconnect(ReadOnlySpan<byte> payload)
    {
        var r = new IpcReader(payload);
        if (r.Remaining < 4) return ProtocolFail(IpcMessageTypes.DisconnectResponse, ResultCode.ERR_INVALID_MSG);
        var chId = r.ReadU32();
        state.RemoveChannel(chId);
        return (IpcMessageTypes.DisconnectResponse, OkResultPayload());
    }

    // ---------------- Messages ----------------

    // Request payload: u32 channelId, u32 numMsgs, u32 timeoutMs
    // Response: u32 result, u32 numActuallyRead, then numActuallyRead PASSTHRU_MSG records
    private (byte, byte[]) ReadMsgs(ReadOnlySpan<byte> payload)
    {
        var r = new IpcReader(payload);
        if (r.Remaining < 12) return ProtocolFail(IpcMessageTypes.ReadMsgsResponse, ResultCode.ERR_INVALID_MSG);
        var chId = r.ReadU32();
        var requestedRaw = r.ReadU32();
        var timeoutMs = (int)r.ReadU32();

        // Reject malformed counts early - a u32 cast straight to int would let
        // a host-controlled value > Int32.MaxValue allocate a multi-GB list.
        if (requestedRaw == 0 || requestedRaw > MaxMsgsPerCall)
            return ProtocolFail(IpcMessageTypes.ReadMsgsResponse, ResultCode.ERR_INVALID_MSG);
        int requested = (int)requestedRaw;

        if (!state.TryGetChannel(chId, out var ch))
            return ProtocolFail(IpcMessageTypes.ReadMsgsResponse, ResultCode.ERR_INVALID_CHANNEL_ID);

        // ISO15765 channels deliver reassembled USDT payloads from the per-channel
        // IsoChannel queue, not raw CAN frames. Wait/dequeue uses the IsoChannel's
        // semaphore + queue so multi-frame responses surface as a single PassThruMsg.
        var msgs = new List<PassThruMsg>(requested);
        var queue = ch.RxQueue;
        var available = ch.RxAvailable;
        if (ch.Protocol == ProtocolID.ISO15765 && ch.IsoChannel is Iso15765Channel iso)
        {
            queue = iso.ReassembledPayloadQueue;
            available = iso.ReassembledAvailable;
        }

        // First, drain everything already in the queue without sleeping. Each
        // dequeued frame "consumes" the matching Release the producer did,
        // even if Wait() never observed it (Wait+TryDequeue and the producer's
        // Enqueue+Release race in either order). We rebalance after the loop.
        while (msgs.Count < requested && queue.TryDequeue(out var m))
            msgs.Add(m);

        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (msgs.Count < requested)
        {
            int remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
            if (remaining == 0 && timeoutMs > 0) break;
            try
            {
                // Block until a producer releases the semaphore or timeout fires.
                // Cancellation isn't plumbed here; the dispatcher is per-call sync
                // and the host-side timeoutMs is the only ceiling.
                if (!available.Wait(timeoutMs == 0 ? 0 : remaining)) break;
            }
            catch (ObjectDisposedException) { break; }   // channel torn down mid-wait
            // Drain again - Release count and queue length aren't synchronised one-to-one
            // (an Enqueue can land between our TryDequeue and the next Wait).
            while (msgs.Count < requested && queue.TryDequeue(out var m))
                msgs.Add(m);
        }

        var w = new IpcWriter();
        var rc = msgs.Count == 0 ? ResultCode.ERR_BUFFER_EMPTY
               : msgs.Count < requested ? ResultCode.ERR_TIMEOUT
               : ResultCode.STATUS_NOERROR;
        w.WriteU32((uint)rc);
        w.WriteU32((uint)msgs.Count);
        foreach (var m in msgs) w.WritePassThruMsg(m);
        return (IpcMessageTypes.ReadMsgsResponse, w.ToArray());
    }

    // Request payload: u32 channelId, u32 numMsgs, u32 timeoutMs, then PASSTHRU_MSG[]
    private (byte, byte[]) WriteMsgs(ReadOnlySpan<byte> payload)
    {
        var r = new IpcReader(payload);
        if (r.Remaining < 12) return ProtocolFail(IpcMessageTypes.WriteMsgsResponse, ResultCode.ERR_INVALID_MSG);
        var chId = r.ReadU32();
        var numMsgsRaw = r.ReadU32();
        _ = r.ReadU32();                                  // timeoutMs (unused - sim is instant)

        if (numMsgsRaw > MaxMsgsPerCall)
            return ProtocolFail(IpcMessageTypes.WriteMsgsResponse, ResultCode.ERR_INVALID_MSG);
        int numMsgs = (int)numMsgsRaw;

        if (!state.TryGetChannel(chId, out var ch))
            return ProtocolFail(IpcMessageTypes.WriteMsgsResponse, ResultCode.ERR_INVALID_CHANNEL_ID);

        int accepted = 0;
        ResultCode rcOverride = ResultCode.STATUS_NOERROR;

        for (int i = 0; i < numMsgs; i++)
        {
            var m = r.ReadPassThruMsg();
            if (ch.Protocol == ProtocolID.ISO15765 && ch.IsoChannel is Iso15765Channel iso)
            {
                var rc = WriteMsgIso15765(iso, ch, m.Data);
                if (rc != ResultCode.STATUS_NOERROR) { rcOverride = rc; break; }
            }
            else
            {
                state.Bus.DispatchHostTx(m.Data, ch);
            }
            accepted++;
        }

        var w = new IpcWriter();
        w.WriteU32((uint)rcOverride);
        w.WriteU32((uint)accepted);
        return (IpcMessageTypes.WriteMsgsResponse, w.ToArray());
    }

    // ISO15765 path: parse [4-byte CAN ID][user payload], drive the TX state
    // machine through the cascade, and translate the terminal N_Result into a
    // J2534 ResultCode.
    private ResultCode WriteMsgIso15765(Iso15765Channel iso, ChannelSession ch, byte[] data)
    {
        if (data.Length < 4)
            return ResultCode.ERR_INVALID_MSG;

        uint canId = ((uint)data[0] << 24) | ((uint)data[1] << 16)
                   | ((uint)data[2] << 8)  | data[3];
        var userPayload = data.AsSpan(4);

        if (userPayload.Length == 0)
            return ResultCode.ERR_INVALID_MSG;

        var begin = iso.BeginTransmit(canId, userPayload);
        if (!begin.Started || begin.Filter == null || begin.CanFrame == null)
            return ResultCode.ERR_NO_FLOW_CONTROL;

        NResult? nResult;
        try
        {
            // Dispatch the first frame (SF or FF). For SF the TX is already Done.
            // For FF, the in-process cascade runs synchronously through ECU's
            // reassembler -> emits FC -> our IsoChannel routes to TX -> CFs -> ...
            // until the message completes or aborts.
            state.Bus.DispatchHostTx(begin.CanFrame, ch);
        }
        finally
        {
            nResult = iso.GetTxResult(begin.Filter);
            iso.EndTransmit(begin.Filter);
        }

        return nResult switch
        {
            NResult.N_OK            => ResultCode.STATUS_NOERROR,
            NResult.N_TIMEOUT_Bs    => ResultCode.ERR_TIMEOUT,
            NResult.N_TIMEOUT_A     => ResultCode.ERR_TIMEOUT,
            NResult.N_BUFFER_OVFLW  => ResultCode.ERR_BUFFER_OVERFLOW,
            NResult.N_INVALID_FS    => ResultCode.ERR_FAILED,
            _                       => ResultCode.ERR_TIMEOUT,    // null / N_ERROR / unknown
        };
    }

    private (byte, byte[]) StartFilter(ReadOnlySpan<byte> payload)
    {
        var r = new IpcReader(payload);
        if (r.Remaining < 8) return ProtocolFail(IpcMessageTypes.StartFilterResponse, ResultCode.ERR_INVALID_MSG);
        var chId = r.ReadU32();
        var filterType = (FilterType)r.ReadU32();
        // 3 PASSTHRU_MSG records: mask, pattern, flow-control. flow-control may be empty
        // (DataSize=0) for non-FLOW_CONTROL filters.
        var maskMsg = r.ReadPassThruMsg();
        var patternMsg = r.ReadPassThruMsg();
        var flowCtlMsg = r.ReadPassThruMsg();

        if (!state.TryGetChannel(chId, out var ch))
            return ProtocolFail(IpcMessageTypes.StartFilterResponse, ResultCode.ERR_INVALID_CHANNEL_ID);

        var filterId = state.AllocateFilterId();

        // FLOW_CONTROL_FILTER on an ISO15765 channel registers an N_AI route in
        // the per-channel TP context. The first 4 bytes of mask/pattern/flowctl
        // Data are the BE CAN ID; for mixed addressing a 5th byte carries N_AE.
        if (ch.Protocol == ProtocolID.ISO15765 &&
            ch.IsoChannel is Iso15765Channel iso &&
            filterType == FilterType.FLOW_CONTROL_FILTER &&
            maskMsg.Data.Length >= 4 && patternMsg.Data.Length >= 4 && flowCtlMsg.Data.Length >= 4)
        {
            iso.AddFilter(new Iso15765Channel.IsoFilter
            {
                Id = filterId,
                MaskCanId = ReadBeCanId(maskMsg.Data),
                PatternCanId = ReadBeCanId(patternMsg.Data),
                FlowCtlCanId = ReadBeCanId(flowCtlMsg.Data),
                FlowCtlExt = flowCtlMsg.Data.Length > 4 ? flowCtlMsg.Data[4] : (byte)0,
                Format = AddressFormat.Normal,
            });
        }
        else
        {
            // Legacy CAN-channel filter list (raw frame mask/pattern matching).
            _ = flowCtlMsg;
            ch.AddFilter(new ChannelFilter
            {
                Id = filterId,
                Type = filterType,
                Mask = maskMsg.Data,
                Pattern = patternMsg.Data,
            });
        }

        var w = new IpcWriter();
        w.WriteU32((uint)ResultCode.STATUS_NOERROR);
        w.WriteU32(filterId);
        return (IpcMessageTypes.StartFilterResponse, w.ToArray());
    }

    private static uint ReadBeCanId(ReadOnlySpan<byte> data)
        => ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];

    private (byte, byte[]) StopFilter(ReadOnlySpan<byte> payload)
    {
        var r = new IpcReader(payload);
        if (r.Remaining < 8) return ProtocolFail(IpcMessageTypes.StopFilterResponse, ResultCode.ERR_INVALID_MSG);
        var chId = r.ReadU32();
        var filterId = r.ReadU32();
        if (state.TryGetChannel(chId, out var ch))
        {
            // Either filter table may own the id; remove from both - missing-id
            // removal is a no-op so we don't bother dispatching by protocol.
            ch.RemoveFilter(filterId);
            if (ch.IsoChannel is Iso15765Channel iso) iso.RemoveFilter(filterId);
        }
        return (IpcMessageTypes.StopFilterResponse, OkResultPayload());
    }

    // Request: [u32 channelId][u32 intervalMs][PassThruMsg]
    // Response: [u32 resultCode][u32 periodicId]
    private (byte, byte[]) StartPeriodic(ReadOnlySpan<byte> payload)
    {
        var r = new IpcReader(payload);
        if (r.Remaining < 8)
            return ProtocolFail(IpcMessageTypes.StartPeriodicResponse, ResultCode.ERR_INVALID_MSG);
        uint channelId  = r.ReadU32();
        uint intervalMs = r.ReadU32();
        PassThruMsg msg;
        try { msg = r.ReadPassThruMsg(); }
        catch { return ProtocolFail(IpcMessageTypes.StartPeriodicResponse, ResultCode.ERR_INVALID_MSG); }

        if (!state.TryGetChannel(channelId, out var ch))
            return ProtocolFail(IpcMessageTypes.StartPeriodicResponse, ResultCode.ERR_INVALID_CHANNEL_ID);

        uint periodicId = state.AllocatePeriodicId();

        // HW $3E delegation is a simulator-wide policy (ECU > "Drive HW $3E
        // keepalives" menu item). When off, the registration is accepted but
        // no timer is created - the P3C session will not be maintained for
        // hosts that delegate via PassThruStartPeriodicMsg.
        byte[] frameData = msg.Data;
        bool createTimer = state.Bus.AllowPeriodicTesterPresent;

        var diag = state.Bus.LogDiagnostic;
        if (diag != null)
        {
            string canIdStr = frameData.Length >= Core.Transport.CanFrame.IdBytes
                ? Core.Transport.CanFrame.ReadId(frameData).ToString("X3")
                : "(short)";
            string hex = HexFormat.Bytes(frameData);
            diag($"[periodic] register chan={channelId} id={periodicId} interval={intervalMs}ms target={canIdStr} " +
                 (createTimer ? "TIMER CREATED" : "SKIPPED (HW $3E disabled)") +
                 $" data={hex}");
        }

        if (createTimer)
        {
            var timer = new Timer(_ =>
            {
                if (state.TryGetChannel(channelId, out var ch))
                    state.Bus.DispatchHostTx(frameData, ch);
            }, null, (int)intervalMs, (int)intervalMs);
            state.AddPeriodicTimer(periodicId, channelId, timer);
        }

        var w = new IpcWriter();
        w.WriteU32((uint)ResultCode.STATUS_NOERROR);
        w.WriteU32(periodicId);
        return (IpcMessageTypes.StartPeriodicResponse, w.ToArray());
    }

    // Request: [u32 channelId][u32 periodicId]
    private (byte, byte[]) StopPeriodic(ReadOnlySpan<byte> payload)
    {
        var r = new IpcReader(payload);
        if (r.Remaining >= 8)
        {
            _ = r.ReadU32();             // channelId - unused; we look up by periodicId
            uint periodicId = r.ReadU32();
            state.RemovePeriodicTimer(periodicId);
            state.Bus.LogDiagnostic?.Invoke($"[periodic] unregister id={periodicId}");
        }
        return (IpcMessageTypes.StopPeriodicResponse, OkResultPayload());
    }

    private (byte, byte[]) SetVoltage(ReadOnlySpan<byte> payload)
        => (IpcMessageTypes.SetVoltageResponse, OkResultPayload());

    // Request: [u32 channelId][u32 ioctlId][u32 inputLen][bytes inputBytes]
    // Response: [u32 resultCode][u32 outputLen][bytes outputBytes]
    //
    // Per-IoctlID input/output payload layouts (in/out are nested inside the
    // inputBytes/outputBytes blobs above):
    //   0x01 GET_CONFIG  : in=[u32 nP][nP × u32 paramId]                      out=[u32 nP][nP × u32 value]
    //   0x02 SET_CONFIG  : in=[u32 nP][nP × (u32 paramId, u32 value)]          out=empty
    //   0x03 READ_VBATT  : in=empty                                           out=[u32 mV]
    //   0x04 FIVE_BAUD   : (rejected - pre-CAN init, ERR_NOT_SUPPORTED)
    //   0x05 FAST_INIT   : (rejected - KWP2000 init, ERR_NOT_SUPPORTED)
    //   0x07 CLEAR_TX    : in=empty                                           out=empty
    //   0x08 CLEAR_RX    : drains RxQueue                                      out=empty
    //   0x09 CLEAR_PER   : cancels all periodic timers for this channel        out=empty
    //   0x0A CLEAR_FLT   : clears the channel's filter table                   out=empty
    //   0x0B CLEAR_FUNC  : clears functional addr lookup table                 out=empty
    //   0x0C ADD_FUNC    : in=[u32 n][n bytes]                                 out=empty
    //   0x0D DEL_FUNC    : in=[u32 n][n bytes]                                 out=empty
    //   0x0E READ_PROG_V : in=empty                                           out=[u32 mV]  (always 0 for sim)
    private (byte, byte[]) Ioctl(ReadOnlySpan<byte> payload)
    {
        var r = new IpcReader(payload);
        if (r.Remaining < 12) return IoctlFail(ResultCode.ERR_INVALID_MSG);
        uint channelId = r.ReadU32();
        uint ioctlId   = r.ReadU32();
        uint inLen     = r.ReadU32();
        if (r.Remaining < inLen) return IoctlFail(ResultCode.ERR_INVALID_MSG);
        var inBytes = inLen > 0 ? r.ReadBytes((int)inLen) : Array.Empty<byte>();

        // Device-level Ioctls (READ_VBATT, READ_PROG_VOLTAGE) are tolerated
        // even if the channel handle isn't valid - the J2534 spec calls them
        // through PassThruIoctl(deviceId, ...) but the host wrapper passes
        // whatever ID it has.
        bool isDeviceLevel = ioctlId == 0x03 || ioctlId == 0x0E;
        ChannelSession? ch = null;
        if (!isDeviceLevel)
        {
            if (!state.TryGetChannel(channelId, out var got))
                return IoctlFail(ResultCode.ERR_INVALID_CHANNEL_ID);
            ch = got;
        }

        var ow = new IpcWriter();
        ResultCode rc = ResultCode.STATUS_NOERROR;

        try
        {
            switch (ioctlId)
            {
                case 0x01: // GET_CONFIG
                {
                    var ir = new IpcReader(inBytes);
                    if (ir.Remaining < 4) { rc = ResultCode.ERR_INVALID_MSG; break; }
                    uint numParams = ir.ReadU32();
                    if (ir.Remaining < numParams * 4) { rc = ResultCode.ERR_INVALID_MSG; break; }
                    ow.WriteU32(numParams);
                    for (int i = 0; i < numParams; i++)
                        ow.WriteU32(ReadConfigParam(ch!, ir.ReadU32()));
                    break;
                }
                case 0x02: // SET_CONFIG
                {
                    var ir = new IpcReader(inBytes);
                    if (ir.Remaining < 4) { rc = ResultCode.ERR_INVALID_MSG; break; }
                    uint numParams = ir.ReadU32();
                    if (ir.Remaining < numParams * 8) { rc = ResultCode.ERR_INVALID_MSG; break; }
                    for (int i = 0; i < numParams; i++)
                    {
                        uint paramId = ir.ReadU32();
                        uint value   = ir.ReadU32();
                        ApplyConfigParam(ch!, paramId, value);
                    }
                    break;
                }
                case 0x03: // READ_VBATT
                    ow.WriteU32(13800);
                    break;
                case 0x07: // CLEAR_TX_BUFFER - no real Tx buffer; writes go straight through
                    break;
                case 0x08: // CLEAR_RX_BUFFER
                    ch!.ClearRxQueue();
                    break;
                case 0x09: // CLEAR_PERIODIC_MSGS
                    state.StopAllPeriodicForChannel(channelId);
                    break;
                case 0x0A: // CLEAR_MSG_FILTERS
                    ch!.ClearFilters();
                    if (ch.IsoChannel is Iso15765Channel isoForClear) isoForClear.ClearFilters();
                    break;
                case 0x0B: // CLEAR_FUNCT_MSG_LOOKUP_TABLE
                case 0x0C: // ADD_TO_FUNCT_MSG_LOOKUP_TABLE
                case 0x0D: // DELETE_FROM_FUNCT_MSG_LOOKUP_TABLE
                    // No-op: functional dispatch routes by hardcoded $101 + ECU's
                    // FunctionalExtAddr, so the host's lookup-table bookkeeping has
                    // no effect on routing. Accept the IOCTL silently.
                    _ = ch;
                    break;
                case 0x0E: // READ_PROG_VOLTAGE - software simulator: no programming pin
                    ow.WriteU32(0);
                    break;
                case 0x04: // FIVE_BAUD_INIT
                case 0x05: // FAST_INIT
                default:
                    rc = ResultCode.ERR_NOT_SUPPORTED;
                    break;
            }
        }
        catch (InvalidDataException)
        {
            rc = ResultCode.ERR_INVALID_MSG;
        }

        var w = new IpcWriter();
        w.WriteU32((uint)rc);
        if (rc == ResultCode.STATUS_NOERROR)
        {
            var outBytes = ow.AsSpan();
            w.WriteU32((uint)outBytes.Length);
            w.WriteBytes(outBytes);
        }
        else
        {
            w.WriteU32(0);
        }
        return (IpcMessageTypes.IoctlResponse, w.ToArray());
    }

    private static (byte, byte[]) IoctlFail(ResultCode rc)
    {
        var w = new IpcWriter();
        w.WriteU32((uint)rc);
        w.WriteU32(0);
        return (IpcMessageTypes.IoctlResponse, w.ToArray());
    }

    private (byte, byte[]) GetLastError(ReadOnlySpan<byte> payload)
    {
        var w = new IpcWriter();
        w.WriteU32((uint)ResultCode.STATUS_NOERROR);
        w.WriteStringU16Length("No error.");
        return (IpcMessageTypes.GetLastErrorResponse, w.ToArray());
    }

    // ---------------- Helpers ----------------

    // J2534-1 v04.04 §7.2.7 ConfigParameter IDs we map to the per-channel
    // ISO 15765-2 timing object. Other IDs fall through to the legacy
    // generic dictionary on ChannelSession (kept for back-compat with
    // non-ISO15765 paths and any unknown vendor params).
    private const uint CFG_ISO15765_BS      = 0x1E;
    private const uint CFG_ISO15765_STMIN   = 0x1F;
    private const uint CFG_ISO15765_WFT_MAX = 0x25;

    private static void ApplyConfigParam(ChannelSession ch, uint paramId, uint value)
    {
        if (ch.IsoChannel is Iso15765Channel iso)
        {
            switch (paramId)
            {
                case CFG_ISO15765_BS:      iso.Timing.BlockSizeSend = (byte)(value & 0xFF); return;
                case CFG_ISO15765_STMIN:   iso.Timing.StMinSendRaw  = (byte)(value & 0xFF); return;
                case CFG_ISO15765_WFT_MAX: iso.Timing.NWftMax       = (int)value;            return;
            }
        }
        ch.SetConfig(paramId, value);
    }

    private static uint ReadConfigParam(ChannelSession ch, uint paramId)
    {
        if (ch.IsoChannel is Iso15765Channel iso)
        {
            switch (paramId)
            {
                case CFG_ISO15765_BS:      return iso.Timing.BlockSizeSend;
                case CFG_ISO15765_STMIN:   return iso.Timing.StMinSendRaw;
                case CFG_ISO15765_WFT_MAX: return (uint)iso.Timing.NWftMax;
            }
        }
        return ch.GetConfig(paramId);
    }

    private static byte[] OkResultPayload()
    {
        var w = new IpcWriter();
        w.WriteU32((uint)ResultCode.STATUS_NOERROR);
        return w.ToArray();
    }

    private static (byte, byte[]) ProtocolFail(byte responseType, ResultCode rc)
    {
        var w = new IpcWriter();
        w.WriteU32((uint)rc);
        return (responseType, w.ToArray());
    }

    private static (byte, byte[]) Failed(byte requestType, ResultCode rc)
    {
        var w = new IpcWriter();
        w.WriteU32((uint)rc);
        return ((byte)(requestType | 0x80), w.ToArray());
    }
}
