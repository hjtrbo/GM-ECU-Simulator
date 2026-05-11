# Final end-to-end test: load the actual native PassThruShim64.dll the way a
# real J2534 host (DataLogger, Tech 2 Win, etc.) would, and exercise it.
# Proves the full chain: native exports -> IPC -> simulator -> bus -> ECU
# -> response -> simulator -> IPC -> native -> caller.

$ErrorActionPreference = "Stop"

$shimPath = (Resolve-Path "PassThruShim\x64\Debug\PassThruShim64.dll").Path
$exe = "GmEcuSimulator\bin\Debug\net9.0-windows\GmEcuSimulator.exe"

# P/Invoke definitions for the J2534 entry points exercised below.
# PASSTHRU_MSG must match the C struct layout exactly (Pack=1).
$source = @"
using System;
using System.Runtime.InteropServices;
using System.Text;

[StructLayout(LayoutKind.Sequential, Pack=1)]
public struct PASSTHRU_MSG {
    public uint ProtocolID;
    public uint RxStatus;
    public uint TxFlags;
    public uint Timestamp;
    public uint DataSize;
    public uint ExtraDataIndex;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst=4128)]
    public byte[] Data;
}

public static class J2534Pinvoke {
    [DllImport(@"$shimPath", CallingConvention=CallingConvention.StdCall)]
    public static extern int PassThruOpen(IntPtr pName, ref uint pDeviceID);

    [DllImport(@"$shimPath", CallingConvention=CallingConvention.StdCall)]
    public static extern int PassThruClose(uint DeviceID);

    [DllImport(@"$shimPath", CallingConvention=CallingConvention.StdCall)]
    public static extern int PassThruConnect(uint DeviceID, uint ProtocolID, uint Flags, uint Baudrate, ref uint pChannelID);

    [DllImport(@"$shimPath", CallingConvention=CallingConvention.StdCall)]
    public static extern int PassThruDisconnect(uint ChannelID);

    [DllImport(@"$shimPath", CallingConvention=CallingConvention.StdCall)]
    public static extern int PassThruWriteMsgs(uint ChannelID, ref PASSTHRU_MSG pMsg, ref uint pNumMsgs, uint Timeout);

    [DllImport(@"$shimPath", CallingConvention=CallingConvention.StdCall)]
    public static extern int PassThruReadMsgs(uint ChannelID, ref PASSTHRU_MSG pMsg, ref uint pNumMsgs, uint Timeout);

    [DllImport(@"$shimPath", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Ansi)]
    public static extern int PassThruReadVersion(uint DeviceID, StringBuilder fw, StringBuilder dll, StringBuilder api);

    [DllImport(@"$shimPath", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Ansi)]
    public static extern int PassThruGetLastError(StringBuilder err);
}
"@
Add-Type -TypeDefinition $source -Language CSharp

$proc = Start-Process -FilePath $exe -PassThru -WindowStyle Minimized
"Sim PID: $($proc.Id)"
Start-Sleep -Milliseconds 1500

try {
    Write-Host ""
    Write-Host "[1] PassThruOpen"
    $deviceId = [uint32]0
    $rc = [J2534Pinvoke]::PassThruOpen([IntPtr]::Zero, [ref]$deviceId)
    "  rc=0x$('{0:X8}' -f $rc)  deviceId=$deviceId"
    if ($rc -ne 0) { throw "Open failed" }

    Write-Host ""
    Write-Host "[2] PassThruConnect (CAN, 500000)"
    $channelId = [uint32]0
    $rc = [J2534Pinvoke]::PassThruConnect($deviceId, 5, 0, 500000, [ref]$channelId)
    "  rc=0x$('{0:X8}' -f $rc)  channelId=$channelId"
    if ($rc -ne 0) { throw "Connect failed" }

    Write-Host ""
    Write-Host "[3] PassThruReadVersion"
    $fw = New-Object System.Text.StringBuilder 80
    $dll = New-Object System.Text.StringBuilder 80
    $api = New-Object System.Text.StringBuilder 80
    $rc = [J2534Pinvoke]::PassThruReadVersion($deviceId, $fw, $dll, $api)
    "  rc=0x$('{0:X8}' -f $rc)  fw=$($fw.ToString())  api=$($api.ToString())"
    if ($rc -ne 0) { throw "ReadVersion failed" }

    Write-Host ""
    Write-Host "[4] PassThruWriteMsgs - send 0x22 ECM PID 0x1234 request"
    $tx = New-Object PASSTHRU_MSG
    $tx.ProtocolID = 5
    $tx.Data = New-Object byte[] 4128
    # CAN ID 0x7E0 (OBD-II ECM) + PCI 0x03 + SID 0x22 + PID 0x1234
    $tx.Data[0] = 0; $tx.Data[1] = 0; $tx.Data[2] = 0x07; $tx.Data[3] = 0xE0  # ECM CAN $7E0
    $tx.Data[4] = 0x03; $tx.Data[5] = 0x22; $tx.Data[6] = 0x12; $tx.Data[7] = 0x34
    $tx.DataSize = 8
    $numTx = [uint32]1
    $rc = [J2534Pinvoke]::PassThruWriteMsgs($channelId, [ref]$tx, [ref]$numTx, 100)
    "  rc=0x$('{0:X8}' -f $rc)  accepted=$numTx"
    if ($rc -ne 0) { throw "WriteMsgs failed" }

    Start-Sleep -Milliseconds 50

    Write-Host ""
    Write-Host "[5] PassThruReadMsgs - drain response"
    $rx = New-Object PASSTHRU_MSG
    $rx.Data = New-Object byte[] 4128
    $numRx = [uint32]1
    $rc = [J2534Pinvoke]::PassThruReadMsgs($channelId, [ref]$rx, [ref]$numRx, 200)
    "  rc=0x$('{0:X8}' -f $rc)  numRead=$numRx  dataSize=$($rx.DataSize)"
    if ($numRx -lt 1) { throw "Expected at least 1 message" }
    $hex = ""
    for ($i = 0; $i -lt $rx.DataSize; $i++) { $hex += ('{0:X2} ' -f $rx.Data[$i]) }
    "  Frame: $hex"
    # Validate: USDT response from ECM. CAN ID 0x641, PCI 0x05, SID 0x62, PID echo 0x1234
    if ($rx.Data[2] -ne 0x07 -or $rx.Data[3] -ne 0xE8) { throw "Wrong response CAN ID" }
    if ($rx.Data[4] -ne 0x05 -or $rx.Data[5] -ne 0x62) { throw "Wrong PCI/SID" }
    if ($rx.Data[6] -ne 0x12 -or $rx.Data[7] -ne 0x34) { throw "Wrong PID echo" }
    $rawValue = ([int]$rx.Data[8] * 256) + [int]$rx.Data[9]
    $engValue = $rawValue * 0.0625 - 40.0
    Write-Host ("  PASSED: response decoded raw={0} ({1:F2} degC)" -f $rawValue, $engValue)

    Write-Host ""
    Write-Host "[6] PassThruDisconnect / PassThruClose"
    [void][J2534Pinvoke]::PassThruDisconnect($channelId)
    [void][J2534Pinvoke]::PassThruClose($deviceId)

    Write-Host ""
    Write-Host "PASSED: full native PassThru round-trip through PassThruShim64.dll."
} finally {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}
