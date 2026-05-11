// Forces the singleton TimerScheduler polling thread to exit when the test
// assembly unloads. Without this, xUnit's testhost can't shut down cleanly
// (the high-priority spin thread keeps the process alive past the testhost
// timeout, which dotnet test reports as "Test host process crashed" even
// though every test passed).
//
// The trick: register a [ModuleInitializer] that subscribes to
// AppDomain.CurrentDomain.ProcessExit and signals the scheduler to stop.
// AppDomain.ProcessExit fires earlier than the testhost's hard kill, so the
// spin loop has a chance to honour the request.
using System.Runtime.CompilerServices;
using Core.Utilities;

namespace EcuSimulator.Tests;

internal static class TestAssemblyShutdown
{
    [ModuleInitializer]
    public static void Init()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TimerSchedulerTestHooks.Shutdown();
        AppDomain.CurrentDomain.DomainUnload += (_, _) => TimerSchedulerTestHooks.Shutdown();
    }
}
