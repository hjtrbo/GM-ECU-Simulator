# Step 7 verification: $3E TesterPresent + $20 ReturnToNormal + P3C timeout
$ErrorActionPreference = "Stop"

. "$PSScriptRoot\test_helpers.ps1"

$exe = "GmEcuSimulator\bin\Debug\net9.0-windows\GmEcuSimulator.exe"
$proc = Start-Process -FilePath $exe -PassThru -WindowStyle Minimized
"Sim PID: $($proc.Id)"
Start-Sleep -Milliseconds 1500

try {
    $pipe = Open-Pipe
    $deviceId = PassThru-Open $pipe
    $channelId = PassThru-Connect $pipe $deviceId 5 500000
    "deviceId=$deviceId channelId=$channelId"

    # ---------- TEST A: $3E physical -> $7E positive response ----------
    Write-Host ""
    Write-Host "[A] 0x3E physical to ECM (CAN 0x7E0, OBD-II) -> expect 0x7E"
    $req = [byte[]](0x00, 0x00, 0x07, 0xE0, 0x01, 0x3E)
    $resp = WriteRead-Single $pipe $channelId $req
    "  Resp: $(Hex $resp)"
    if ($resp[4] -ne 0x01 -or $resp[5] -ne 0x7E) { throw "Expected SF len 1, 0x7E" }
    Write-Host "  PASSED: 0x7E positive response"

    # ---------- TEST B: $3E functional (CAN 0x101 ext 0xFE) -> silent ----------
    Write-Host ""
    Write-Host "[B] 0x3E functional (CAN 0x101, ext 0xFE) -> expect SILENCE"
    $req = [byte[]](0x00, 0x00, 0x01, 0x01, 0xFE, 0x01, 0x3E)
    [void](WriteMsgs $pipe $channelId $req)
    Start-Sleep -Milliseconds 100
    $payload = Concat-Bytes @((Pack-U32 $channelId), (Pack-U32 1), (Pack-U32 50))
    Write-Frame $pipe 0x05 $payload
    $r = Read-Frame $pipe
    $numRead = U32 $r.Payload 4
    if ($numRead -gt 0) { throw "Expected silence, got $numRead frames" }
    Write-Host "  PASSED: functional 0x3E generated no response"

    # ---------- TEST C: $20 physical -> $60 positive response ----------
    Write-Host ""
    Write-Host "[C] 0x20 ReturnToNormal physical -> expect 0x60"
    $req = [byte[]](0x00, 0x00, 0x07, 0xE0, 0x01, 0x20)
    $resp = WriteRead-Single $pipe $channelId $req
    "  Resp: $(Hex $resp)"
    if ($resp[4] -ne 0x01 -or $resp[5] -ne 0x60) { throw "Expected SF len 1, 0x60" }
    Write-Host "  PASSED: 0x60 positive response"

    # ---------- TEST D: P3C timeout drops scheduler + sends unsolicited 0x60 ----------
    Write-Host ""
    Write-Host "[D] P3C timeout: schedule 0xAA Fast, then go silent for 6s -> expect"
    Write-Host "    scheduler stops within ~5s and unsolicited 0x60 lands"

    # First, define a DPID and start Fast scheduling
    $req2c = [byte[]](0x00, 0x00, 0x07, 0xE0, 0x06, 0x2C, 0xFE, 0x12, 0x34, 0x56, 0x78)
    [void](WriteRead-Single $pipe $channelId $req2c)
    $reqAA = [byte[]](0x00, 0x00, 0x07, 0xE0, 0x03, 0xAA, 0x04, 0xFE)
    [void](WriteMsgs $pipe $channelId $reqAA)
    Write-Host "  0xAA Fast scheduled. Going silent for 6s..."

    # Wait 6 seconds — well past P3C nominal (5000ms)
    $start = Get-Date
    Start-Sleep -Milliseconds 6000

    # Drain everything that arrived. We expect:
    #   - some Fast UUDT frames from before the timeout (0x541 ... 0xFE ...)
    #   - one unsolicited 0x60 frame on 0x641 (from Exit_Diagnostic_Services)
    #   - then silence
    $framesAfterTimeout = 0
    $sawUnsolicited60 = $false
    $sawFastFrame = $false
    $framesBeforeStop = 0
    for ($i = 0; $i -lt 200; $i++) {
        $payload = Concat-Bytes @((Pack-U32 $channelId), (Pack-U32 1), (Pack-U32 50))
        Write-Frame $pipe 0x05 $payload
        $r = Read-Frame $pipe
        $numRead = U32 $r.Payload 4
        if ($numRead -eq 0) { break }
        $off = 8 + 20
        $dataSize = U32 $r.Payload $off
        $off += 4
        $data = $r.Payload[$off..($off + $dataSize - 1)]
        # UUDT Fast frame: CAN 0x541, byte[4] = 0xFE
        if ($data[2] -eq 0x05 -and $data[3] -eq 0xE8 -and $data[4] -eq 0xFE) {
            $sawFastFrame = $true
            $framesBeforeStop++
        }
        # USDT $60: CAN 0x641, byte[4] = 0x01 (PCI), byte[5] = 0x60
        elseif ($data[2] -eq 0x07 -and $data[3] -eq 0xE8 -and $data[4] -eq 0x01 -and $data[5] -eq 0x60) {
            $sawUnsolicited60 = $true
            "  Got unsolicited 0x60: $(Hex $data)"
        }
    }
    "  Drained $framesBeforeStop Fast frames + unsolicited60=$sawUnsolicited60"
    if (-not $sawFastFrame) { throw "Expected some Fast frames before timeout, got none" }
    if (-not $sawUnsolicited60) { throw "Expected unsolicited 0x60 after P3C timeout, got none" }
    Write-Host "  PASSED: P3C timeout fired, scheduler cleared, unsolicited 0x60 emitted"

    # ---------- TEST E: After P3C, scheduler is silent ----------
    Write-Host ""
    Write-Host "[E] After P3C: 500ms quiet period should be silent"
    Start-Sleep -Milliseconds 500
    $payload = Concat-Bytes @((Pack-U32 $channelId), (Pack-U32 1), (Pack-U32 50))
    Write-Frame $pipe 0x05 $payload
    $r = Read-Frame $pipe
    $newFrames = U32 $r.Payload 4
    if ($newFrames -gt 0) { throw "Expected silence, got $newFrames frames" }
    Write-Host "  PASSED: scheduler silent after P3C"

    $pipe.Close()
    Write-Host ""
    Write-Host "All step-7 tests PASSED."
} finally {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}
