using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Common.PassThru;
using Core.Bus;
using Core.Ipc;
using Core.Persistence;

namespace EcuSimulator.Tests.Integration;

// Loads the actual native PassThruShim64.dll via P/Invoke and exercises it
// against an in-process NamedPipeServer — closing the loop the way a real
// J2534 host (DataLogger, Tech 2 Win) would. Skipped by default in `dotnet
// test` because loading a native DLL into the xUnit testhost destabilises
// its shutdown sequence; the equivalent end-to-end coverage lives in
// `Tests/test_native_shim.ps1` which runs the simulator EXE + shim DLL out
// of process. Run this xUnit test directly when iterating on the shim:
//   dotnet test --filter "FullyQualifiedName~NativeShim"
public class NativeShimTests : IAsyncLifetime
{
    private VirtualBus bus = null!;
    private NamedPipeServer server = null!;
    private IntPtr libHandle;
    private bool shimAvailable;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PASSTHRU_MSG_NATIVE
    {
        public uint ProtocolID;
        public uint RxStatus;
        public uint TxFlags;
        public uint Timestamp;
        public uint DataSize;
        public uint ExtraDataIndex;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4128)]
        public byte[] Data;
    }

    private delegate int Open_t(IntPtr name, ref uint deviceId);
    private delegate int Close_t(uint deviceId);
    private delegate int Connect_t(uint deviceId, uint protocolId, uint flags, uint baud, ref uint channelId);
    private delegate int Disconnect_t(uint channelId);
    private delegate int WriteMsgs_t(uint channelId, ref PASSTHRU_MSG_NATIVE msg, ref uint numMsgs, uint timeout);
    private delegate int ReadMsgs_t(uint channelId, ref PASSTHRU_MSG_NATIVE msg, ref uint numMsgs, uint timeout);
    private delegate int ReadVersion_t(uint deviceId, StringBuilder fw, StringBuilder dll, StringBuilder api);

    private Open_t? open;
    private Close_t? close;
    private Connect_t? connect;
    private Disconnect_t? disconnect;
    private WriteMsgs_t? writeMsgs;
    private ReadMsgs_t? readMsgs;
    private ReadVersion_t? readVersion;

    public Task InitializeAsync()
    {
        // Locate the shim DLL relative to the test assembly. The build doesn't copy
        // it automatically because msbuild for the C++ project is a separate step.
        var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        // Walk up until we find PassThruShim/x64/Debug/PassThruShim64.dll
        var candidate = FindShim(asmDir);
        shimAvailable = candidate != null && File.Exists(candidate);
        if (!shimAvailable) return Task.CompletedTask;

        bus = new VirtualBus();
        DefaultEcuConfig.ApplyIfEmpty(bus);
        bus.Scheduler.Start();
        server = new NamedPipeServer(bus, _ => { });
        server.Start();
        // Give the accept loop a moment to materialise the first
        // NamedPipeServerStream before the shim's CreateFileW tries to connect.
        Thread.Sleep(100);

        libHandle = NativeLibrary.Load(candidate!);
        open        = LoadDelegate<Open_t>("PassThruOpen");
        close       = LoadDelegate<Close_t>("PassThruClose");
        connect     = LoadDelegate<Connect_t>("PassThruConnect");
        disconnect  = LoadDelegate<Disconnect_t>("PassThruDisconnect");
        writeMsgs   = LoadDelegate<WriteMsgs_t>("PassThruWriteMsgs");
        readMsgs    = LoadDelegate<ReadMsgs_t>("PassThruReadMsgs");
        readVersion = LoadDelegate<ReadVersion_t>("PassThruReadVersion");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (libHandle != IntPtr.Zero) NativeLibrary.Free(libHandle);
        if (server != null) await server.DisposeAsync();
    }

    private T LoadDelegate<T>(string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(libHandle, name));

    private static string? FindShim(string startDir)
    {
        var dir = startDir;
        for (int i = 0; i < 10 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "PassThruShim", "x64", "Debug", "PassThruShim64.dll");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // Skip rationale: the test passes when run alone, but loading the shim's
    // C++ static state into the xUnit testhost prevents the host from shutting
    // down cleanly afterwards (the run is reported as "aborted" even though
    // every test passed). Run via `dotnet test --filter
    // FullyQualifiedName~NativeShim` when iterating on the shim, or use the
    // PowerShell equivalent at Tests/test_native_shim.ps1 for CI.
    [Fact(Skip = "Run explicitly via --filter; see comment above. PowerShell equivalent: Tests/test_native_shim.ps1.")]
    public void OpenConnectReadVersionWriteRead_FullChain()
    {
        if (!shimAvailable)
        {
            // Don't fail the suite if the shim isn't built; skip with a clear message.
            // Rebuild via: msbuild PassThruShim/PassThruShim.vcxproj /p:Platform=x64
            return;
        }

        // Open
        uint deviceId = 0;
        Assert.Equal(0, open!(IntPtr.Zero, ref deviceId));

        // Connect (CAN, 500000 baud)
        uint channelId = 0;
        Assert.Equal(0, connect!(deviceId, 5, 0, 500000, ref channelId));

        // ReadVersion
        var fw = new StringBuilder(80);
        var dll = new StringBuilder(80);
        var api = new StringBuilder(80);
        Assert.Equal(0, readVersion!(deviceId, fw, dll, api));
        Assert.Equal("1.0.0", fw.ToString());
        Assert.Equal("04.04", api.ToString());

        // WriteMsgs: $22 ECM PID 0x1234
        var tx = new PASSTHRU_MSG_NATIVE
        {
            ProtocolID = 5,
            Data = new byte[4128],
            DataSize = 8,
        };
        var req = new byte[] { 0x00, 0x00, 0x07, 0xE0, 0x03, 0x22, 0x12, 0x34 };  // ECM CAN $7E0
        Array.Copy(req, tx.Data, 8);
        uint numTx = 1;
        Assert.Equal(0, writeMsgs!(channelId, ref tx, ref numTx, 100));

        Thread.Sleep(50);

        // ReadMsgs
        var rx = new PASSTHRU_MSG_NATIVE { Data = new byte[4128] };
        uint numRx = 1;
        Assert.Equal(0, readMsgs!(channelId, ref rx, ref numRx, 200));
        Assert.Equal(1u, numRx);

        // Validate: USDT response on $7E8, $62 0x1234 + 2-byte big-endian value.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x07, 0xE8, 0x05, 0x62, 0x12, 0x34 }, rx.Data[..8]);

        Assert.Equal(0, disconnect!(channelId));
        Assert.Equal(0, close!(deviceId));
    }
}
