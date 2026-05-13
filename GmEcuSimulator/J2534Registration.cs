using System.IO;
using Microsoft.Win32;

namespace GmEcuSimulator;

/// <summary>
/// Reads the J2534 v04.04 HKLM entries that PassThru hosts consult when
/// they enumerate installed devices. No elevation is needed for the read;
/// the matching Register.ps1 / Unregister.ps1 scripts that write these keys
/// run elevated via UAC.
///
/// Single source of truth: App.OnStartup uses Check() to decide whether to
/// start the IPC pipe; MainViewModel.RefreshJ2534Status uses it to drive
/// the status banner.
/// </summary>
internal static class J2534Registration
{
    private const string Key32 = @"SOFTWARE\WOW6432Node\PassThruSupport.04.04\GmEcuSim";
    private const string Key64 = @"SOFTWARE\PassThruSupport.04.04\GmEcuSim";

    public sealed record Status(bool Has32, bool Has64)
    {
        public bool IsRegistered => Has32 || Has64;
    }

    /// <summary>
    /// Reads HKLM. A registry entry counts as registered only if its
    /// FunctionLibrary value points at a file that exists on disk - a
    /// stale path left over from an uninstall would otherwise pass.
    /// </summary>
    public static Status Check()
    {
        using var k32 = Registry.LocalMachine.OpenSubKey(Key32);
        using var k64 = Registry.LocalMachine.OpenSubKey(Key64);
        var has32 = k32?.GetValue("FunctionLibrary") is string p32 && File.Exists(p32);
        var has64 = k64?.GetValue("FunctionLibrary") is string p64 && File.Exists(p64);
        return new Status(has32, has64);
    }
}
