# Same end-to-end check as test_j2534sharp_open.ps1, but exercises the
# 32-bit shim. Must be invoked from a 32-bit PowerShell process — usually
# %SystemRoot%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe — because a
# 64-bit process cannot LoadLibrary a 32-bit DLL.
#
# Caller is responsible for starting GmEcuSimulator.exe (the named-pipe
# server is bitness-agnostic, so the 64-bit simulator process serves the
# 32-bit shim just fine).

$ErrorActionPreference = "Stop"

if ([IntPtr]::Size -ne 4) {
    "FAILED: this script must run in 32-bit PowerShell (currently running as $([IntPtr]::Size * 8)-bit)"
    "Use: %SystemRoot%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -File <this-script>"
    exit 1
}

$repoRoot   = Split-Path -Parent $PSScriptRoot
$dll        = Join-Path $repoRoot "PassThruShim\Debug\PassThruShim32.dll"
$j2534Sharp = "C:\Users\Nathan\OneDrive\ECA\Resources\Visual Studio\Gm Data Logger_v5_Wpf_WIP\Core\J2534-Sharp.dll"

"PowerShell bitness: $([IntPtr]::Size * 8)-bit"
"DLL: $dll"

Add-Type -Path $j2534Sharp

try {
    $api = [SAE.J2534.APIFactory]::GetAPI($dll)
    "GetAPI OK"

    $dev = $api.GetDevice('')
    "GetDevice('') OK -> $($dev.GetType().FullName)"

    $ch = $dev.GetChannel([SAE.J2534.Protocol]::CAN, [SAE.J2534.Baud]::CAN, [SAE.J2534.ConnectFlag]::NONE, $false)
    "GetChannel(CAN, 500k) OK -> $($ch.GetType().FullName)"

    $ch.Dispose()
    $dev.Dispose()
    $api.Dispose()
    "PASSED: 32-bit shim opened the simulator"
} catch {
    "FAILED: $($_.Exception.GetType().FullName): $($_.Exception.Message)"
    if ($_.Exception.InnerException) {
        "  Inner: $($_.Exception.InnerException.Message)"
    }
}
