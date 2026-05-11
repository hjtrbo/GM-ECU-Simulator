# Verifies J2534-Sharp can open the simulator's device + acquire a CAN
# channel without going through GetDeviceList (which is broken on x64 in
# this build of J2534-Sharp). This is the workaround pattern host code
# should use: skip the device list and call GetDevice("") directly.

$ErrorActionPreference = "Stop"

$exe = "GmEcuSimulator\bin\Debug\net9.0-windows\GmEcuSimulator.exe"
$dll = (Resolve-Path "PassThruShim\x64\Debug\PassThruShim64.dll").Path
$j2534Sharp = "C:\Users\Nathan\OneDrive\ECA\Resources\Visual Studio\Gm Data Logger_v5_Wpf_WIP\Core\J2534-Sharp.dll"

$sim = Start-Process -FilePath $exe -PassThru -WindowStyle Minimized
"Sim PID: $($sim.Id)"
Start-Sleep -Milliseconds 1500

try {
    Add-Type -Path $j2534Sharp
    $api = [SAE.J2534.APIFactory]::GetAPI($dll)
    "GetAPI OK"

    # Skip GetDeviceList entirely (J2534-Sharp can't enumerate without the
    # buggy CarDAQ path). Call GetDevice('') for the v04.04 default device.
    $dev = $api.GetDevice('')
    "GetDevice('') OK -> $($dev.GetType().FullName)"

    $ch = $dev.GetChannel([SAE.J2534.Protocol]::CAN, [SAE.J2534.Baud]::CAN, [SAE.J2534.ConnectFlag]::NONE, $false)
    "GetChannel(CAN, 500k) OK -> $($ch.GetType().FullName)"

    $ch.Dispose()
    $dev.Dispose()
    $api.Dispose()
    "PASSED: opened the simulator without going through GetDeviceList"
} catch {
    "FAILED: $($_.Exception.GetType().FullName): $($_.Exception.Message)"
    if ($_.Exception.InnerException) {
        "  Inner: $($_.Exception.InnerException.Message)"
    }
} finally {
    Stop-Process -Id $sim.Id -Force -ErrorAction SilentlyContinue
}
