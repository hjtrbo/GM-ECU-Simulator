# Diagnostic: lists every J2534 PassThru device currently registered on
# this machine, in both the 64-bit and 32-bit (WOW6432Node) registry views.
# Read-only - does not change anything. Run this BEFORE Register/Unregister
# to capture the baseline, and AFTER to confirm what changed.
#
# A J2534 host iterates the immediate children of PassThruSupport.04.04 and
# reads the standard values (Name, Vendor, FunctionLibrary, ProtocolsSupported,
# CAN, ISO15765, ...) directly off each child. Any subkey that does not have
# those values isn't a usable device.

$ErrorActionPreference = "Continue"

function Show-View([string]$root, [string]$label) {
    Write-Host ""
    Write-Host "=== $label ===" -ForegroundColor Cyan
    Write-Host "  $root"
    if (-not (Test-Path $root)) {
        Write-Host "  (key does not exist - no J2534 devices registered for this bitness)" -ForegroundColor Yellow
        return
    }
    $vendors = Get-ChildItem $root -ErrorAction SilentlyContinue
    if ($null -eq $vendors -or $vendors.Count -eq 0) {
        Write-Host "  (empty)" -ForegroundColor Yellow
        return
    }
    foreach ($v in $vendors) {
        $vp               = $v.PSPath
        $name             = (Get-ItemProperty $vp -Name "Name"               -ErrorAction SilentlyContinue).Name
        $vendor           = (Get-ItemProperty $vp -Name "Vendor"             -ErrorAction SilentlyContinue).Vendor
        $funcLib          = (Get-ItemProperty $vp -Name "FunctionLibrary"    -ErrorAction SilentlyContinue).FunctionLibrary
        $protos           = (Get-ItemProperty $vp -Name "ProtocolsSupported" -ErrorAction SilentlyContinue).ProtocolsSupported
        $hasFlat          = $null -ne $funcLib

        Write-Host ""
        Write-Host "  [$($v.PSChildName)]" -ForegroundColor White
        if ($hasFlat) {
            Write-Host "    Name:               $name"
            Write-Host "    Vendor:             $vendor"
            Write-Host "    FunctionLibrary:    $funcLib"
            Write-Host "    ProtocolsSupported: $protos"
            if (Test-Path $funcLib) {
                Write-Host "    DLL exists:         YES" -ForegroundColor Green
            } else {
                Write-Host "    DLL exists:         NO  (host will skip this device)" -ForegroundColor Red
            }
        } else {
            # No values directly on the vendor key - likely deeper nesting.
            $children = Get-ChildItem $vp -ErrorAction SilentlyContinue
            if ($null -ne $children -and $children.Count -gt 0) {
                Write-Host "    (no FunctionLibrary at this level - has $($children.Count) sub-key(s):" -ForegroundColor Yellow
                foreach ($c in $children) { Write-Host "      $($c.PSChildName)" }
                Write-Host "    NOTE: Most J2534 hosts only look one level deep. Devices nested" -ForegroundColor Yellow
                Write-Host "          under a sub-key may be invisible to those hosts.)" -ForegroundColor Yellow
            } else {
                Write-Host "    (empty - no values, no sub-keys)" -ForegroundColor Yellow
            }
        }
    }
}

Write-Host "J2534 PassThru registry inventory" -ForegroundColor Cyan
Show-View "HKLM:\SOFTWARE\PassThruSupport.04.04"             "64-bit view (used by 64-bit J2534 hosts)"
Show-View "HKLM:\SOFTWARE\WOW6432Node\PassThruSupport.04.04" "32-bit view (used by 32-bit J2534 hosts)"
Write-Host ""
