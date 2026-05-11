# Drive a brief burst of J2534 traffic through the running simulator so the
# Bus log tab populates for the manual screenshot. Connects to the EXISTING
# simulator (does NOT spawn a new one).

$ErrorActionPreference = 'Stop'
$root = "C:\Users\Nathan\OneDrive\ECA\Resources\Visual Studio\GM ECU Simulator"
$shimPath = Join-Path $root 'PassThruShim\x64\Debug\PassThruShim64.dll'
if (-not (Test-Path $shimPath)) { throw "Shim not found at $shimPath" }

$source = @"
using System;
using System.Runtime.InteropServices;
using System.Text;

[StructLayout(LayoutKind.Sequential, Pack=1)]
public struct PMSG {
    public uint ProtocolID;
    public uint RxStatus;
    public uint TxFlags;
    public uint Timestamp;
    public uint DataSize;
    public uint ExtraDataIndex;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst=4128)]
    public byte[] Data;
}

public static class J {
    [DllImport(@"$shimPath", CallingConvention=CallingConvention.StdCall)]
    public static extern int PassThruOpen(IntPtr pName, ref uint pDeviceID);
    [DllImport(@"$shimPath", CallingConvention=CallingConvention.StdCall)]
    public static extern int PassThruClose(uint DeviceID);
    [DllImport(@"$shimPath", CallingConvention=CallingConvention.StdCall)]
    public static extern int PassThruConnect(uint DeviceID, uint ProtocolID, uint Flags, uint Baudrate, ref uint pChannelID);
    [DllImport(@"$shimPath", CallingConvention=CallingConvention.StdCall)]
    public static extern int PassThruDisconnect(uint ChannelID);
    [DllImport(@"$shimPath", CallingConvention=CallingConvention.StdCall)]
    public static extern int PassThruWriteMsgs(uint ChannelID, ref PMSG pMsg, ref uint pNumMsgs, uint Timeout);
    [DllImport(@"$shimPath", CallingConvention=CallingConvention.StdCall)]
    public static extern int PassThruReadMsgs(uint ChannelID, ref PMSG pMsg, ref uint pNumMsgs, uint Timeout);
}
"@
Add-Type -TypeDefinition $source -Language CSharp -ErrorAction SilentlyContinue

function New-Msg([byte[]]$body) {
    $m = New-Object PMSG
    $m.ProtocolID = 6   # ISO15765
    $m.TxFlags    = 0x100  # CAN_29BIT_ID off; treat as 11-bit
    $m.Data = New-Object byte[] 4128
    [Array]::Copy($body, 0, $m.Data, 0, $body.Length)
    $m.DataSize = $body.Length
    return $m
}

$devId = 0
$rc = [J]::PassThruOpen([IntPtr]::Zero, [ref]$devId)
"Open rc=$rc dev=$devId"

$chId = 0
$rc = [J]::PassThruConnect($devId, 6, 0, 500000, [ref]$chId)
"Connect rc=$rc ch=$chId"

# Send a few ReadDataByIdentifier ($22) requests to ECM (0x7E0)
# Frame: 4-byte CAN ID prefix + ISO-TP single frame
# SF: 0x03 0x22 0x00 0x01  -> request PID 0x0001 (Engine RPM)
$pidIds = @(0x0001, 0x0002, 0x0001, 0x0002, 0x0001)
foreach ($pidId in $pidIds) {
    $hi = [byte](($pidId -shr 8) -band 0xFF)
    $lo = [byte]($pidId -band 0xFF)
    $body = [byte[]]@(0x00, 0x00, 0x07, 0xE0, 0x03, 0x22, $hi, $lo)
    $m = New-Msg $body
    $n = [uint32]1
    $rc = [J]::PassThruWriteMsgs($chId, [ref]$m, [ref]$n, 200)
    Start-Sleep -Milliseconds 60

    $rm = New-Object PMSG
    $rm.Data = New-Object byte[] 4128
    $rn = [uint32]1
    [J]::PassThruReadMsgs($chId, [ref]$rm, [ref]$rn, 200) | Out-Null
}

# TesterPresent ($3E)
$tp = [byte[]]@(0x00,0x00,0x07,0xE0, 0x02, 0x3E, 0x00)
$m = New-Msg $tp; $n = [uint32]1
[J]::PassThruWriteMsgs($chId, [ref]$m, [ref]$n, 200) | Out-Null

Start-Sleep -Milliseconds 200
"Wrote $($pidIds.Length) PID requests + 1 TesterPresent"

[J]::PassThruDisconnect($chId) | Out-Null
[J]::PassThruClose($devId) | Out-Null
"Done."
