# Registers the GM ECU Simulator as a J2534 PassThru device.
#
# J2534-1 v04.04 device discovery is registry-driven. The standard layout
# is FLAT: each immediate subkey of PassThruSupport.04.04 IS a device, with
# all values (Name / Vendor / FunctionLibrary / ProtocolsSupported / per-
# protocol DWORD flags) on that subkey directly. We register one entry per
# bitness:
#   HKLM\SOFTWARE\PassThruSupport.04.04\GmEcuSim          (64-bit hosts)
#   HKLM\SOFTWARE\WOW6432Node\PassThruSupport.04.04\GmEcuSim (32-bit hosts)
#
# Earlier versions of this script created an extra "Device1" sub-level -
# wrong layout that most hosts do not follow. The apply path here strips
# any legacy nesting before writing the flat values, and the uninstall
# path removes both layouts.
#
# Run elevated. Pass -Build to rebuild both shim DLLs first.

[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

# --- Check elevation -------------------------------------------------------
# Both Register and Unregister write to HKLM and need admin. Checking up
# front gives a clean error before we do anything destructive.

$id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object System.Security.Principal.WindowsPrincipal($id)
if (-not $pr.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Register.ps1 must be run elevated (Administrator) to write HKLM."
}

# --- Resolve paths ---------------------------------------------------------

$repoRoot = Split-Path -Parent $PSScriptRoot
$shim64 = Join-Path $repoRoot "PassThruShim\x64\Debug\PassThruShim64.dll"
$shim32 = Join-Path $repoRoot "PassThruShim\Debug\PassThruShim32.dll"
$exe    = Join-Path $repoRoot "GmEcuSimulator\bin\Debug\net9.0-windows\GmEcuSimulator.exe"

# --- Optional build --------------------------------------------------------

if ($Build) {
    $msbuild = "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    if (-not (Test-Path $msbuild)) { throw "MSBuild not found at $msbuild" }

    Write-Host "Building 64-bit shim..."
    & $msbuild "$repoRoot\PassThruShim\PassThruShim.vcxproj" /p:Configuration=Debug /p:Platform=x64 /nologo /v:minimal | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "MSBuild x64 failed (exit $LASTEXITCODE)" }
    Write-Host "Building 32-bit shim..."
    & $msbuild "$repoRoot\PassThruShim\PassThruShim.vcxproj" /p:Configuration=Debug /p:Platform=Win32 /nologo /v:minimal | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "MSBuild Win32 failed (exit $LASTEXITCODE)" }
    Write-Host "Building C# simulator..."
    & dotnet build "$repoRoot\GM ECU Simulator.sln" -c Debug | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
}

# --- Validate ---------------------------------------------------------------

if (-not $Uninstall) {
    foreach ($p in @($shim64, $shim32, $exe)) {
        if (-not (Test-Path $p)) { throw "Required artifact missing: $p (run with -Build first)" }
    }
}

# --- Registry layout (FLAT - values directly on the device subkey) ---------

$key64 = "HKLM:\SOFTWARE\PassThruSupport.04.04\GmEcuSim"
$key32 = "HKLM:\SOFTWARE\WOW6432Node\PassThruSupport.04.04\GmEcuSim"

# Old (wrong) layout this script previously wrote - apply path cleans them
# before writing the flat values; uninstall removes them recursively.
$oldKey64 = "HKLM:\SOFTWARE\PassThruSupport.04.04\GmEcuSim\Device1"
$oldKey32 = "HKLM:\SOFTWARE\WOW6432Node\PassThruSupport.04.04\GmEcuSim\Device1"

function Set-Device([string]$key, [string]$shimPath) {
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-ItemProperty -Path $key -Name "Name"               -Value "GM ECU Simulator"
    Set-ItemProperty -Path $key -Name "Vendor"             -Value "hjtrbo"
    Set-ItemProperty -Path $key -Name "ConfigApplication"  -Value $exe
    Set-ItemProperty -Path $key -Name "FunctionLibrary"    -Value $shimPath
    Set-ItemProperty -Path $key -Name "ProtocolsSupported" -Value "CAN,ISO15765"
    Set-ItemProperty -Path $key -Name "CAN"                -Value 1 -Type DWord
    Set-ItemProperty -Path $key -Name "ISO15765"           -Value 1 -Type DWord
    Write-Host "  Registered: $key -> $shimPath"
}

function Remove-Device([string]$key) {
    if (Test-Path $key) {
        # Recurse so any old "Device1" sub-key from the prior layout goes too.
        Remove-Item $key -Recurse -Force
        Write-Host "  Removed: $key"
    } else {
        Write-Host "  Not present: $key"
    }
}

# --- Uninstall path ---------------------------------------------------------

if ($Uninstall) {
    Write-Host "Removing GM ECU Simulator J2534 entries (only - no other vendors are touched)..."
    Remove-Device $key64
    Remove-Device $key32
    Write-Host ""
    Write-Host "Done. Run Installer\List.ps1 to confirm your other J2534 DLL entries are still present."
    return
}

# --- Apply ------------------------------------------------------------------

Write-Host "Registering GM ECU Simulator..."

# Strip residue from the old (wrong) two-level layout before writing the
# flat one. Without this the legacy "Device1" sub-key would linger
# underneath the now-flat GmEcuSim entry.
foreach ($old in @($oldKey64, $oldKey32)) {
    if (Test-Path $old) {
        Remove-Item $old -Recurse -Force
        Write-Host "  Cleaned up legacy nested key: $old"
    }
}

Set-Device $key64 $shim64
Set-Device $key32 $shim32
Write-Host ""
Write-Host "Done. J2534 hosts should now discover 'GM ECU Simulator' as a device."
Write-Host "Run Installer\List.ps1 to verify the layout."
