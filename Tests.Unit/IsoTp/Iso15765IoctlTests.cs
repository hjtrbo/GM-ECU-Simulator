using Common.PassThru;
using Common.Wire;
using Core.Bus;
using Shim.Ipc;
using Shim.IsoTp;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// Verifies that J2534 IOCTL SET_CONFIG / GET_CONFIG carries ISO15765 timing
// parameters (ISO15765_BS, ISO15765_STMIN, ISO15765_WFT_MAX) all the way
// through to the per-channel Iso15765Channel.Timing object - the value the
// fragmenter and reassembler use when they emit FCs.
public class Iso15765IoctlTests
{
    private const uint CFG_ISO15765_BS      = 0x1E;
    private const uint CFG_ISO15765_STMIN   = 0x1F;
    private const uint CFG_ISO15765_WFT_MAX = 0x25;

    private const uint IOCTL_GET_CONFIG = 0x01;
    private const uint IOCTL_SET_CONFIG = 0x02;

    private static (uint channelId, IpcSessionState state, RequestDispatcher dispatcher)
        OpenIso15765Channel()
    {
        var bus = new VirtualBus();
        var state = new IpcSessionState(bus);
        var dispatcher = new RequestDispatcher(state);

        var connect = new IpcWriter();
        connect.WriteU32(0);                                     // deviceId (unused)
        connect.WriteU32((uint)ProtocolID.ISO15765);
        connect.WriteU32(0);                                     // flags
        connect.WriteU32(500_000);                               // baud
        var (_, resp) = dispatcher.Dispatch(IpcMessageTypes.ConnectRequest, connect.AsSpan());
        var rr = new IpcReader(resp);
        var rc = rr.ReadU32();
        Assert.Equal((uint)ResultCode.STATUS_NOERROR, rc);
        return (rr.ReadU32(), state, dispatcher);
    }

    private static byte[] BuildIoctl(uint channelId, uint ioctlId, ReadOnlySpan<byte> input)
    {
        var w = new IpcWriter();
        w.WriteU32(channelId);
        w.WriteU32(ioctlId);
        w.WriteU32((uint)input.Length);
        w.WriteBytes(input);
        return w.ToArray();
    }

    private static byte[] BuildSetConfigInput(params (uint paramId, uint value)[] pairs)
    {
        var w = new IpcWriter();
        w.WriteU32((uint)pairs.Length);
        foreach (var (id, v) in pairs) { w.WriteU32(id); w.WriteU32(v); }
        return w.ToArray();
    }

    private static byte[] BuildGetConfigInput(params uint[] paramIds)
    {
        var w = new IpcWriter();
        w.WriteU32((uint)paramIds.Length);
        foreach (var id in paramIds) w.WriteU32(id);
        return w.ToArray();
    }

    // -----------------------------------------------------------------------
    // SET_CONFIG flows ISO15765 params into Iso15765Channel.Timing
    // -----------------------------------------------------------------------

    [Fact]
    public void SET_CONFIG_writes_ISO15765_BS_into_channel_Timing()
    {
        var (channelId, state, dispatcher) = OpenIso15765Channel();
        var input = BuildSetConfigInput((CFG_ISO15765_BS, 0xAB));

        var (rt, resp) = dispatcher.Dispatch(IpcMessageTypes.IoctlRequest,
            BuildIoctl(channelId, IOCTL_SET_CONFIG, input));

        Assert.Equal(IpcMessageTypes.IoctlResponse, rt);
        Assert.Equal((uint)ResultCode.STATUS_NOERROR, new IpcReader(resp).ReadU32());

        Assert.True(state.TryGetChannel(channelId, out var ch));
        var iso = Assert.IsType<Iso15765Channel>(ch.IsoChannel);
        Assert.Equal((byte)0xAB, iso.Timing.BlockSizeSend);
    }

    [Fact]
    public void SET_CONFIG_writes_ISO15765_STMIN_into_channel_Timing()
    {
        var (channelId, state, dispatcher) = OpenIso15765Channel();
        // STmin = 10 ms (raw 0x0A) per Table 20.
        var input = BuildSetConfigInput((CFG_ISO15765_STMIN, 0x0A));

        dispatcher.Dispatch(IpcMessageTypes.IoctlRequest,
            BuildIoctl(channelId, IOCTL_SET_CONFIG, input));

        state.TryGetChannel(channelId, out var ch);
        var iso = (Iso15765Channel)ch.IsoChannel!;
        Assert.Equal((byte)0x0A, iso.Timing.StMinSendRaw);
    }

    [Fact]
    public void SET_CONFIG_writes_ISO15765_WFT_MAX_into_channel_Timing()
    {
        var (channelId, state, dispatcher) = OpenIso15765Channel();
        var input = BuildSetConfigInput((CFG_ISO15765_WFT_MAX, 5));

        dispatcher.Dispatch(IpcMessageTypes.IoctlRequest,
            BuildIoctl(channelId, IOCTL_SET_CONFIG, input));

        state.TryGetChannel(channelId, out var ch);
        var iso = (Iso15765Channel)ch.IsoChannel!;
        Assert.Equal(5, iso.Timing.NWftMax);
    }

    [Fact]
    public void SET_CONFIG_with_multiple_ISO15765_params_writes_all()
    {
        var (channelId, state, dispatcher) = OpenIso15765Channel();
        var input = BuildSetConfigInput(
            (CFG_ISO15765_BS, 0xFF),
            (CFG_ISO15765_STMIN, 0x14),
            (CFG_ISO15765_WFT_MAX, 3));

        dispatcher.Dispatch(IpcMessageTypes.IoctlRequest,
            BuildIoctl(channelId, IOCTL_SET_CONFIG, input));

        state.TryGetChannel(channelId, out var ch);
        var iso = (Iso15765Channel)ch.IsoChannel!;
        Assert.Equal((byte)0xFF, iso.Timing.BlockSizeSend);
        Assert.Equal((byte)0x14, iso.Timing.StMinSendRaw);
        Assert.Equal(3, iso.Timing.NWftMax);
    }

    // -----------------------------------------------------------------------
    // GET_CONFIG reads ISO15765 params back from Iso15765Channel.Timing
    // -----------------------------------------------------------------------

    [Fact]
    public void GET_CONFIG_reads_back_what_SET_CONFIG_wrote()
    {
        var (channelId, _, dispatcher) = OpenIso15765Channel();
        dispatcher.Dispatch(IpcMessageTypes.IoctlRequest, BuildIoctl(channelId, IOCTL_SET_CONFIG,
            BuildSetConfigInput((CFG_ISO15765_BS, 0x42), (CFG_ISO15765_STMIN, 0x14))));

        var (_, resp) = dispatcher.Dispatch(IpcMessageTypes.IoctlRequest,
            BuildIoctl(channelId, IOCTL_GET_CONFIG,
                BuildGetConfigInput(CFG_ISO15765_BS, CFG_ISO15765_STMIN)));

        var rr = new IpcReader(resp);
        Assert.Equal((uint)ResultCode.STATUS_NOERROR, rr.ReadU32());
        var outLen = rr.ReadU32();
        Assert.True(outLen >= 12);

        var or = new IpcReader(rr.ReadBytes((int)outLen));
        var nP = or.ReadU32();
        Assert.Equal(2u, nP);
        Assert.Equal(0x42u, or.ReadU32());
        Assert.Equal(0x14u, or.ReadU32());
    }

    // -----------------------------------------------------------------------
    // SET_CONFIG of an unknown param falls through to the legacy generic dict
    // (kept for backward compatibility with vendor-specific or non-ISO15765 IDs).
    // -----------------------------------------------------------------------

    [Fact]
    public void SET_CONFIG_unknown_param_does_not_touch_ISO15765_timing()
    {
        var (channelId, state, dispatcher) = OpenIso15765Channel();
        var input = BuildSetConfigInput((0x99, 0xDEADBEEF));     // unknown param

        var (_, resp) = dispatcher.Dispatch(IpcMessageTypes.IoctlRequest,
            BuildIoctl(channelId, IOCTL_SET_CONFIG, input));
        Assert.Equal((uint)ResultCode.STATUS_NOERROR, new IpcReader(resp).ReadU32());

        state.TryGetChannel(channelId, out var ch);
        var iso = (Iso15765Channel)ch.IsoChannel!;
        Assert.Equal((byte)0, iso.Timing.BlockSizeSend);     // defaults intact
        Assert.Equal((byte)0, iso.Timing.StMinSendRaw);
        Assert.Equal(0xDEADBEEFu, ch.GetConfig(0x99));        // stored generically
    }
}
