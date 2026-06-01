using Core.Bus;
using Shim.Ipc;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace EcuSimulator.Tests.RawCan;

// Server mechanics for the raw-CAN TCP listener (no diagnostics): idempotent
// lifecycle, ephemeral-port readback, the HostConnected / HostDisconnected
// signals, and single-connection-at-a-time behaviour.
public sealed class RawCanTcpServerLifecycleTests
{
    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(15);
        }
        return condition();
    }

    [Fact]
    public async Task Start_and_stop_are_idempotent_and_cycle_cleanly()
    {
        var bus = new VirtualBus();
        await using var server = new RawCanTcpServer(bus, port: 0, log: _ => { });

        server.Start();
        Assert.True(server.IsRunning);
        int firstPort = server.Port;
        Assert.True(firstPort > 0);

        server.Start();                       // second Start is a no-op
        Assert.True(server.IsRunning);
        Assert.Equal(firstPort, server.Port);

        await server.StopAsync();
        Assert.False(server.IsRunning);
        await server.StopAsync();             // second StopAsync is a no-op
        Assert.False(server.IsRunning);

        // Stop -> Start re-listens (fresh ephemeral port).
        server.Start();
        Assert.True(server.IsRunning);
        Assert.True(server.Port > 0);
    }

    [Fact]
    public async Task Connecting_and_disconnecting_raise_host_events()
    {
        var bus = new VirtualBus();
        int connected = 0, disconnected = 0;
        bus.HostConnected += () => Interlocked.Increment(ref connected);
        bus.HostDisconnected += () => Interlocked.Increment(ref disconnected);

        await using var server = new RawCanTcpServer(bus, port: 0, log: _ => { });
        server.Start();

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);

        Assert.True(WaitUntil(() => Volatile.Read(ref connected) == 1), "HostConnected was not raised");
        Assert.True(WaitUntil(() => server.IsConnected), "server did not report a connection");

        client.Close();
        Assert.True(WaitUntil(() => Volatile.Read(ref disconnected) == 1), "HostDisconnected was not raised");
        Assert.True(WaitUntil(() => !server.IsConnected), "server still reports a connection");
    }

    [Fact]
    public async Task Services_one_gauge_at_a_time_queuing_the_second()
    {
        var bus = new VirtualBus();
        int connected = 0;
        bus.HostConnected += () => Interlocked.Increment(ref connected);

        await using var server = new RawCanTcpServer(bus, port: 0, log: _ => { });
        server.Start();
        int port = server.Port;

        using var client1 = new TcpClient();
        await client1.ConnectAsync(IPAddress.Loopback, port);
        Assert.True(WaitUntil(() => Volatile.Read(ref connected) == 1), "first gauge not serviced");

        // Second gauge connects at the TCP layer but must NOT be serviced while
        // the first is alive: no second HostConnected within the window.
        using var client2 = new TcpClient();
        await client2.ConnectAsync(IPAddress.Loopback, port);
        Thread.Sleep(300);
        Assert.Equal(1, Volatile.Read(ref connected));

        // Dropping the first lets the queued second through.
        client1.Close();
        Assert.True(WaitUntil(() => Volatile.Read(ref connected) == 2),
            "second gauge was not serviced after the first disconnected");
    }
}
