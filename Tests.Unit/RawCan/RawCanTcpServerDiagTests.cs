using Common.Protocol;
using Common.Waveforms;
using Core.Bus;
using Core.Ecu;
using EcuSimulator.Tests.TestHelpers;
using Shim.Ipc;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace EcuSimulator.Tests.RawCan;

// End-to-end over a real loopback socket: a gauge speaking raw OBD-II / GMW3110
// CAN frames drives the full bus stack (ISO-TP reassembly/fragmentation, the
// service handlers, the DPID scheduler) through RawCanTcpServer. The test code
// IS the gauge's ISO-TP stack, the same shape as ProgrammingSequenceCanProtocol
// tests, but every frame crosses the TCP wire as a 13-byte RawCanWire frame.
public sealed class RawCanTcpServerDiagTests
{
    private const ushort PhysReq = NodeFactory.PhysReq;       // $7E0
    private const ushort Obd2Func = 0x7DF;                    // functional broadcast
    private const ushort UsdtResp = NodeFactory.UsdtResp;     // $7E8
    private const ushort UudtResp = NodeFactory.UudtResp;     // $5E8

    // ---- gauge-side socket + ISO-TP helpers ------------------------------

    private sealed class Gauge : IAsyncDisposable
    {
        private readonly RawCanTcpServer server;
        private readonly TcpClient client;
        private readonly NetworkStream stream;

        private Gauge(RawCanTcpServer server, TcpClient client)
        {
            this.server = server;
            this.client = client;
            stream = client.GetStream();
            stream.ReadTimeout = 3000;
            stream.WriteTimeout = 3000;
        }

        public static async Task<Gauge> ConnectAsync(RawCanTcpServer server)
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, server.Port);
            return new Gauge(server, c);
        }

        public void SendCanFrame(uint canId, ReadOnlySpan<byte> data)
        {
            var internalFrame = new byte[4 + data.Length];
            internalFrame[0] = (byte)((canId >> 24) & 0xFF);
            internalFrame[1] = (byte)((canId >> 16) & 0xFF);
            internalFrame[2] = (byte)((canId >> 8) & 0xFF);
            internalFrame[3] = (byte)(canId & 0xFF);
            data.CopyTo(internalFrame.AsSpan(4));

            var wire = new byte[RawCanWire.FrameSize];
            RawCanWire.FromInternal(internalFrame, wire);
            stream.Write(wire, 0, wire.Length);
            stream.Flush();
        }

        public (uint canId, byte[] data) ReadCanFrame()
        {
            var wire = new byte[RawCanWire.FrameSize];
            stream.ReadExactly(wire, 0, wire.Length);     // throws on timeout/close
            int dlc = wire[0] & 0x0F;
            uint canId = ((uint)wire[1] << 24) | ((uint)wire[2] << 16) | ((uint)wire[3] << 8) | wire[4];
            return (canId, wire.AsSpan(5, dlc).ToArray());
        }

        // Send a USDT payload as SF or FF+CFs (popping the ECU's FC.CTS after FF).
        public void SendUsdt(uint reqCanId, byte[] payload)
        {
            if (payload.Length <= 7)
            {
                var sf = new byte[1 + payload.Length];
                sf[0] = (byte)(payload.Length & 0x0F);
                payload.CopyTo(sf, 1);
                SendCanFrame(reqCanId, sf);
                return;
            }

            int total = payload.Length;            // tests stay <= 4095 (short FF)
            var ff = new byte[8];
            ff[0] = (byte)(0x10 | ((total >> 8) & 0x0F));
            ff[1] = (byte)(total & 0xFF);
            int firstChunk = Math.Min(6, total);
            payload.AsSpan(0, firstChunk).CopyTo(ff.AsSpan(2));
            SendCanFrame(reqCanId, ff);

            // ECU emits FC.CTS - it is the responder's FC, addressed to us.
            var (_, fc) = ReadCanFrame();
            Assert.Equal(0x30, fc[0] & 0xF0);

            int written = firstChunk;
            byte sn = 1;
            while (written < total)
            {
                int chunk = Math.Min(7, total - written);
                var cf = new byte[1 + chunk];
                cf[0] = (byte)(0x20 | (sn & 0x0F));
                payload.AsSpan(written, chunk).CopyTo(cf.AsSpan(1));
                SendCanFrame(reqCanId, cf);
                written += chunk;
                sn = (byte)((sn + 1) & 0x0F);
            }
        }

        // Reassemble a USDT response. On FF, sends FC.CTS (BS=0, STmin=0) back
        // to reqCanId and drains the cascaded CFs.
        public byte[] ReceiveUsdt(uint reqCanId, uint expectRespCanId)
        {
            var (firstCanId, first) = ReadCanFrame();
            Assert.Equal(expectRespCanId, firstCanId);
            int pciHigh = first[0] & 0xF0;

            if (pciHigh == 0x00)
                return first.AsSpan(1, first[0] & 0x0F).ToArray();
            Assert.Equal(0x10, pciHigh);

            int total = ((first[0] & 0x0F) << 8) | first[1];
            var buf = new byte[total];
            int initial = Math.Min(total, first.Length - 2);
            first.AsSpan(2, initial).CopyTo(buf);
            int written = initial;

            SendCanFrame(reqCanId, new byte[] { 0x30, 0x00, 0x00 });

            byte expectSn = 1;
            while (written < total)
            {
                var (cfCanId, cf) = ReadCanFrame();
                Assert.Equal(expectRespCanId, cfCanId);
                Assert.Equal(0x20, cf[0] & 0xF0);
                Assert.Equal(expectSn & 0x0F, cf[0] & 0x0F);
                int chunk = Math.Min(total - written, cf.Length - 1);
                cf.AsSpan(1, chunk).CopyTo(buf.AsSpan(written));
                written += chunk;
                expectSn = (byte)((expectSn + 1) & 0x0F);
            }
            return buf;
        }

        // Reads frames until one on UudtResp carries the given DPID id.
        public byte[] ReadUntilDpid(byte dpid, int maxFrames = 16)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                var (canId, data) = ReadCanFrame();
                if (canId == UudtResp && data.Length >= 1 && data[0] == dpid) return data;
            }
            throw new Xunit.Sdk.XunitException($"no UUDT frame with DPID 0x{dpid:X2} within {maxFrames} frames");
        }

        public async ValueTask DisposeAsync()
        {
            client.Close();
            await server.StopAsync();
        }
    }

    private static (VirtualBus bus, EcuNode node, RawCanTcpServer server) StartWith(EcuNode node)
    {
        var bus = new VirtualBus();
        bus.AddNode(node);
        var server = new RawCanTcpServer(bus, port: 0, log: _ => { });
        server.Start();
        return (bus, node, server);
    }

    // ---- tests ------------------------------------------------------------

    [Theory]
    [InlineData(PhysReq)]      // physical
    [InlineData(Obd2Func)]     // 0x7DF functional broadcast
    public async Task Obd2_mode01_rpm_round_trips(ushort requestId)
    {
        // Fresh node advertises the J1979 catalogue subset; PID $0C (RPM) reads
        // ~750 rpm at idle -> raw (256A+B) ~ 3000 (0x0BB8). The exact value is
        // unspecified here because bus.NowMs has advanced (the direct-handler
        // test pins the precise encoding at timeMs:0); we assert the frame shape
        // and that the value decodes to idle.
        var (_, _, server) = StartWith(NodeFactory.CreateNode());
        await using var gauge = await Gauge.ConnectAsync(server);

        gauge.SendUsdt(requestId, new byte[] { 0x01, 0x0C });
        var resp = gauge.ReceiveUsdt(requestId, UsdtResp);

        Assert.Equal(4, resp.Length);
        Assert.Equal(0x41, resp[0]);                       // Mode 01 positive response
        Assert.Equal(0x0C, resp[1]);                       // echoed PID
        int rawRpm = (resp[2] << 8) | resp[3];
        Assert.InRange(rawRpm, 2800, 3200);                // ~700-800 rpm idle band
    }

    [Fact]
    public async Task Gmw3110_22_single_pid_round_trips()
    {
        var node = NodeFactory.CreateNode();
        node.AddPid(new Pid
        {
            Address = 0x000C,
            Size = PidSize.Byte,
            DataType = PidDataType.Unsigned,
            Scalar = 1.0,
            Offset = 0.0,
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Constant, Offset = 0x42 },
        });
        var (_, _, server) = StartWith(node);
        await using var gauge = await Gauge.ConnectAsync(server);

        gauge.SendUsdt(PhysReq, new byte[] { 0x22, 0x00, 0x0C });
        var resp = gauge.ReceiveUsdt(PhysReq, UsdtResp);

        Assert.Equal(new byte[] { 0x62, 0x00, 0x0C, 0x42 }, resp);
    }

    [Fact]
    public async Task Gmw3110_22_multiframe_response_streams_with_gauge_fc()
    {
        // A 20-byte static PID -> $22 response is [0x62, hi, lo] + 20 = 23 bytes,
        // forcing FF + CFs and exercising the gauge-sends-FC path over the socket.
        var staticBytes = new byte[20];
        for (int i = 0; i < staticBytes.Length; i++) staticBytes[i] = (byte)(0xA0 + i);
        var node = NodeFactory.CreateNode();
        node.AddPid(new Pid { Address = 0x1234, LengthBytes = 20, StaticBytes = staticBytes });
        var (_, _, server) = StartWith(node);
        await using var gauge = await Gauge.ConnectAsync(server);

        gauge.SendUsdt(PhysReq, new byte[] { 0x22, 0x12, 0x34 });
        var resp = gauge.ReceiveUsdt(PhysReq, UsdtResp);

        Assert.Equal(23, resp.Length);
        Assert.Equal(new byte[] { 0x62, 0x12, 0x34 }, resp.AsSpan(0, 3).ToArray());
        Assert.Equal(staticBytes, resp.AsSpan(3).ToArray());
    }

    [Fact]
    public async Task Gmw3110_2C_define_then_AA_streams_uudt_and_stops()
    {
        var node = NodeFactory.CreateNode();
        node.AddPid(new Pid
        {
            Address = 0x000C,
            Size = PidSize.Byte,
            DataType = PidDataType.Unsigned,
            Scalar = 1.0,
            Offset = 0.0,
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Constant, Offset = 0x55 },
        });
        var (_, _, server) = StartWith(node);
        await using var gauge = await Gauge.ConnectAsync(server);

        // $2C define DPID $FE = { PID $000C } -> positive [0x6C, 0xFE].
        gauge.SendUsdt(PhysReq, new byte[] { 0x2C, 0xFE, 0x00, 0x0C });
        Assert.Equal(new byte[] { 0x6C, 0xFE }, gauge.ReceiveUsdt(PhysReq, UsdtResp));

        // $AA $01 sendOneResponse -> exactly one UUDT on $5E8 with [DPID, value].
        gauge.SendUsdt(PhysReq, new byte[] { 0xAA, 0x01, 0xFE });
        var (uudtId, uudt) = gauge.ReadCanFrame();
        Assert.Equal(UudtResp, uudtId);
        Assert.Equal(new byte[] { 0xFE, 0x55 }, uudt);

        // $AA $04 scheduleAtFastRate (~40 ms) -> a stream of UUDT frames.
        gauge.SendUsdt(PhysReq, new byte[] { 0xAA, 0x04, 0xFE });
        for (int i = 0; i < 2; i++)
        {
            var frame = gauge.ReadUntilDpid(0xFE);
            Assert.Equal((byte)0x55, frame[1]);
        }

        // $AA $00 stopSending -> a UUDT with DPID $00 acknowledges the stop
        // (any in-flight fast frames are drained while looking for it).
        gauge.SendUsdt(PhysReq, new byte[] { 0xAA, 0x00 });
        var stopAck = gauge.ReadUntilDpid(0x00);
        Assert.Equal(new byte[] { 0x00 }, stopAck);
    }
}
