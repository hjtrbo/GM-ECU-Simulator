using Common.Protocol;
using Common.Waveforms;
using Core.Bus;
using Core.Ecu;
using Core.Transport;

namespace Core.Services;

// $2D DefinePidByAddress handler. Tester gives us a (PID id, memory address,
// size) triplet; we register a new dynamic PID under the given id that
// inherits the waveform/scaling of whichever statically-configured PID lives
// at the same memory address.
//
// USDT request:
//   byte[0]      = 0x2D
//   bytes[1..2]  = new PID id (16-bit big-endian)
//   bytes[3..N]  = memory address (2, 3, or 4 bytes big-endian, length per CTS)
//   byte[N+1]    = memory size (1..7)
//
// USDT positive response:
//   byte[0]      = 0x6D
//   bytes[1..2]  = PID id echo
//
// Negative responses: $12 SFNS-IF on length error, $31 ROOR on invalid PID
// id range, unsupported address, out-of-range size, or pidId collision with
// a statically-configured PID.
public static class Service2DHandler
{
    /// <summary>Returns true if a positive response was enqueued (caller should
    /// activate P3C). False if an NRC was enqueued.</summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        const byte sid = Service.DefinePidByAddress;
        // Min frame: SID + 2-byte PID + 2-byte MA + 1-byte MS = 6 bytes
        if (usdtPayload.Length < 6 || usdtPayload[0] != sid)
        {
            ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        ushort pidId = (ushort)((usdtPayload[1] << 8) | usdtPayload[2]);

        // Trailing MS byte; everything between the PID id and MS is the address.
        int addrLen = usdtPayload.Length - 1 - 2 - 1;       // total - SID - PID - MS
        if (addrLen < 2 || addrLen > 4)
        {
            ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }
        uint memoryAddress = 0;
        for (int i = 0; i < addrLen; i++)
            memoryAddress = (memoryAddress << 8) | usdtPayload[3 + i];

        byte memorySize = usdtPayload[3 + addrLen];
        if (memorySize < 1 || memorySize > 7)
        {
            ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.RequestOutOfRange);
            return false;
        }

        // Resolve the waveform: must match an existing configured PID at the
        // 32-bit memory address, otherwise the tester is asking about RAM we
        // don't model.
        var existing = node.GetPid(memoryAddress);
        if (existing == null)
        {
            ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.RequestOutOfRange);
            return false;
        }

        // Refuse to define a dynamic PID at an id that collides with an
        // existing static PID. EcuNode.AddPid replaces by address, and on
        // session-end EcuExitLogic removes by address — together they would
        // silently delete the static PID for the rest of the session.
        // (A self-collision where pidId == memoryAddress is fine; that just
        // re-registers the existing PID with possibly-new size.)
        if (pidId != memoryAddress && node.GetPid(pidId) != null)
        {
            ServiceUtil.EnqueueNrc(node, ch, sid, Nrc.RequestOutOfRange);
            return false;
        }

        // Register a new dynamic PID under the requested id. Inherits the
        // existing PID's waveform and scaling; size is whatever the tester asked
        // for (may differ from the static PID's size if they want fewer bytes).
        // The replay factory is propagated BEFORE WaveformConfig is assigned -
        // the WaveformConfig setter rebuilds the active generator and needs
        // the factory in place to produce a ReplayWaveform when Shape ==
        // FileStream. Without this, a $2D alias of a bin-replay PID encodes
        // as ConstantWaveform(0) on the wire.
        var dyn = new Pid
        {
            Address = pidId,
            Name = $"$2D({existing.Name})",
            Size = (Common.Protocol.PidSize)memorySize,
            DataType = existing.DataType,
            Scalar = existing.Scalar,
            Offset = existing.Offset,
            Unit = existing.Unit,
        };
        dyn.SetReplayWaveformFactory(existing.ReplayWaveformFactory);
        dyn.WaveformConfig = existing.WaveformConfig;
        node.AddPid(dyn);
        // Track for cleanup on P3C timeout / $20 ReturnToNormal.
        lock (node.State.DynamicallyDefinedPids) node.State.DynamicallyDefinedPids.Add(pidId);

        // Positive response: 0x6D + PID id echo.
        IsoTpFragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(sid), (byte)(pidId >> 8), (byte)(pidId & 0xFF)]);
        return true;
    }
}
