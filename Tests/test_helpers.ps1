# Shared helpers for the simulator end-to-end test scripts.

function Open-Pipe {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "GmEcuSim.PassThru", [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(3000)
    return $pipe
}

function Pack-U32([UInt32]$v) {
    return [byte[]](($v -band 0xFF), (($v -shr 8) -band 0xFF), (($v -shr 16) -band 0xFF), (($v -shr 24) -band 0xFF))
}

function Concat-Bytes([byte[][]]$arrays) {
    $total = 0
    foreach ($a in $arrays) { $total += $a.Length }
    $out = New-Object byte[] $total
    $off = 0
    foreach ($a in $arrays) {
        [Array]::Copy($a, 0, $out, $off, $a.Length)
        $off += $a.Length
    }
    return $out
}

function Pack-PassThruMsg([UInt32]$protocolId, [byte[]]$data) {
    return Concat-Bytes @(
        (Pack-U32 $protocolId),
        (Pack-U32 0), (Pack-U32 0), (Pack-U32 0), (Pack-U32 0),
        (Pack-U32 ([UInt32]$data.Length)),
        $data)
}

function Write-Frame($pipe, $type, [byte[]]$payload) {
    $len = $payload.Length + 1
    $hdr = [byte[]](($len -band 0xFF), (($len -shr 8) -band 0xFF), (($len -shr 16) -band 0xFF), (($len -shr 24) -band 0xFF), $type)
    $pipe.Write($hdr, 0, 5)
    if ($payload.Length -gt 0) { $pipe.Write($payload, 0, $payload.Length) }
    $pipe.Flush()
}

function Read-Frame($pipe) {
    $hdr = New-Object byte[] 5
    $n = $pipe.Read($hdr, 0, 5)
    if ($n -ne 5) { throw "Short header read: $n" }
    $len = $hdr[0] -bor ($hdr[1] -shl 8) -bor ($hdr[2] -shl 16) -bor ($hdr[3] -shl 24)
    $type = $hdr[4]
    $payload = New-Object byte[] ($len - 1)
    $total = 0
    while ($total -lt $payload.Length) {
        $r = $pipe.Read($payload, $total, $payload.Length - $total)
        if ($r -eq 0) { throw "Short payload read at $total" }
        $total += $r
    }
    return @{ Type = $type; Payload = $payload }
}

function U32([byte[]]$buf, [int]$off) {
    return [System.BitConverter]::ToUInt32($buf, $off)
}

function Hex([byte[]]$bytes) {
    return ($bytes | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
}

function PassThru-Open($pipe) {
    Write-Frame $pipe 0x01 ([byte[]]@())
    $r = Read-Frame $pipe
    if ($r.Type -ne 0x81) { throw "Bad Open response 0x$('{0:X2}' -f $r.Type)" }
    return U32 $r.Payload 4
}

function PassThru-Connect($pipe, [UInt32]$deviceId, [UInt32]$proto, [UInt32]$baud) {
    $payload = Concat-Bytes @((Pack-U32 $deviceId), (Pack-U32 $proto), (Pack-U32 0), (Pack-U32 $baud))
    Write-Frame $pipe 0x03 $payload
    $r = Read-Frame $pipe
    if ($r.Type -ne 0x83) { throw "Bad Connect response 0x$('{0:X2}' -f $r.Type)" }
    $rc = U32 $r.Payload 0
    if ($rc -ne 0) { throw "Connect rc=0x$('{0:X8}' -f $rc)" }
    return U32 $r.Payload 4
}

function WriteMsgs($pipe, [UInt32]$channelId, [byte[]]$frameData) {
    $msg = Pack-PassThruMsg 5 $frameData
    $payload = Concat-Bytes @((Pack-U32 $channelId), (Pack-U32 1), (Pack-U32 100), $msg)
    Write-Frame $pipe 0x06 $payload
    $r = Read-Frame $pipe
    if ($r.Type -ne 0x86) { throw "Bad WriteMsgs response 0x$('{0:X2}' -f $r.Type)" }
    return U32 $r.Payload 0
}

function ReadMsgs($pipe, [UInt32]$channelId, [int]$timeoutMs) {
    $payload = Concat-Bytes @((Pack-U32 $channelId), (Pack-U32 1), (Pack-U32 ([UInt32]$timeoutMs)))
    Write-Frame $pipe 0x05 $payload
    $r = Read-Frame $pipe
    if ($r.Type -ne 0x85) { throw "Bad ReadMsgs response 0x$('{0:X2}' -f $r.Type)" }
    $numRead = U32 $r.Payload 4
    if ($numRead -lt 1) { throw "ReadMsgs returned 0 messages" }
    # Skip 8 (rc + numRead) + 5*4 (PASSTHRU_MSG header fields) = 28 bytes
    $off = 8 + 20
    $dataSize = U32 $r.Payload $off
    $off += 4
    return $r.Payload[$off..($off + $dataSize - 1)]
}

function WriteRead-Single($pipe, [UInt32]$channelId, [byte[]]$frameData) {
    [void](WriteMsgs $pipe $channelId $frameData)
    Start-Sleep -Milliseconds 50
    return ReadMsgs $pipe $channelId 200
}
