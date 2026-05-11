using System.IO.Pipes;
using Common.PassThru;
using Common.Wire;
using Core.Bus;
using Core.Ecu;
using Core.Ipc;
using Core.Persistence;

namespace EcuSimulator.Tests.Integration;

// Spins up the simulator's NamedPipeServer in-process and connects a real
// NamedPipeClientStream — no separate process, no native DLL. This proves
// the full inbound/outbound IPC round-trip with the real pipe transport.
public class IpcEndToEndTests : IAsyncLifetime
{
    private VirtualBus bus = null!;
    private NamedPipeServer server = null!;
    private NamedPipeClientStream pipe = null!;

    public async Task InitializeAsync()
    {
        bus = new VirtualBus();
        DefaultEcuConfig.ApplyIfEmpty(bus);
        bus.Scheduler.Start();

        server = new NamedPipeServer(bus, _ => { });
        server.Start();

        // Connect a client. The pipe name is the production one — tests run
        // serially within a fixture so this is fine.
        pipe = new NamedPipeClientStream(".", NamedPipeServer.PipeName, PipeDirection.InOut);
        await pipe.ConnectAsync(3000);
    }

    public async Task DisposeAsync()
    {
        pipe.Dispose();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadVersion_ReturnsExpectedStrings()
    {
        await FrameTransport.WriteFrameAsync(pipe, IpcMessageTypes.ReadVersionRequest, ReadOnlyMemory<byte>.Empty, default);
        var (type, payload) = await FrameTransport.ReadFrameAsync(pipe, default);
        Assert.Equal(IpcMessageTypes.ReadVersionResponse, type);
        var r = new IpcReader(payload);
        Assert.Equal(0u, r.ReadU32());                        // STATUS_NOERROR
        Assert.Equal("1.0.0", r.ReadStringU16Length());
        Assert.Equal("1.0.0", r.ReadStringU16Length());
        Assert.Equal("04.04", r.ReadStringU16Length());
    }

    [Fact]
    public async Task ConnectThenWriteThenRead_RoundTripsService22()
    {
        // Open
        await FrameTransport.WriteFrameAsync(pipe, IpcMessageTypes.OpenRequest, ReadOnlyMemory<byte>.Empty, default);
        var (_, openResp) = await FrameTransport.ReadFrameAsync(pipe, default);
        var openR = new IpcReader(openResp);
        Assert.Equal(0u, openR.ReadU32());
        var deviceId = openR.ReadU32();

        // Connect (CAN, 500000)
        var connectReq = new IpcWriter();
        connectReq.WriteU32(deviceId);
        connectReq.WriteU32((uint)ProtocolID.CAN);
        connectReq.WriteU32(0);
        connectReq.WriteU32(500000);
        await FrameTransport.WriteFrameAsync(pipe, IpcMessageTypes.ConnectRequest, connectReq.ToArray(), default);
        var (_, connectResp) = await FrameTransport.ReadFrameAsync(pipe, default);
        var connectR = new IpcReader(connectResp);
        Assert.Equal(0u, connectR.ReadU32());
        var channelId = connectR.ReadU32();

        // WriteMsgs: $22 PID 0x1234 to ECM (CAN 0x7E0 — OBD-II 11-bit convention).
        var msg = new PassThruMsg
        {
            ProtocolID = ProtocolID.CAN,
            Data = [0x00, 0x00, 0x07, 0xE0, 0x03, 0x22, 0x12, 0x34],
        };
        var writeReq = new IpcWriter();
        writeReq.WriteU32(channelId);
        writeReq.WriteU32(1);
        writeReq.WriteU32(100);
        writeReq.WritePassThruMsg(msg);
        await FrameTransport.WriteFrameAsync(pipe, IpcMessageTypes.WriteMsgsRequest, writeReq.ToArray(), default);
        var (_, writeResp) = await FrameTransport.ReadFrameAsync(pipe, default);
        Assert.Equal(0u, new IpcReader(writeResp).ReadU32());

        // Give the dispatcher a moment.
        await Task.Delay(50);

        // ReadMsgs: drain the response
        var readReq = new IpcWriter();
        readReq.WriteU32(channelId);
        readReq.WriteU32(1);
        readReq.WriteU32(200);
        await FrameTransport.WriteFrameAsync(pipe, IpcMessageTypes.ReadMsgsRequest, readReq.ToArray(), default);
        var (_, readResp) = await FrameTransport.ReadFrameAsync(pipe, default);
        var readR = new IpcReader(readResp);
        readR.ReadU32();                                            // rc
        Assert.Equal(1u, readR.ReadU32());                         // numActuallyRead
        var got = readR.ReadPassThruMsg();
        // Validate response: USDT response on $7E8, $62 0x1234 + 2-byte value.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x07, 0xE8, 0x05, 0x62, 0x12, 0x34 }, got.Data[..8]);
    }
}
