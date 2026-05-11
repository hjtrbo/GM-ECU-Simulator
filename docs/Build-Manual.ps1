# Build-Manual.ps1
# Captures remaining screenshots and builds the PDF user manual.
# Run once from any working directory; all paths are absolute.

$ErrorActionPreference = 'Stop'
$root  = "C:\Users\Nathan\OneDrive\ECA\Resources\Visual Studio\GM ECU Simulator"
$shots = "$root\docs\manual_screenshots"
$docs  = "$root\docs"

# ---------------------------------------------------------------------------
# P/Invoke helpers
# ---------------------------------------------------------------------------
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$csrc = @'
using System;
using System.Runtime.InteropServices;
public class WH {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hh, uint f);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
    public struct RECT { public int L,T,R,B; }
    public const uint NOSIZE     = 0x0001;
    public const uint NOMOVE     = 0x0002;
    public const uint NOACTIVATE = 0x0010;
    public const uint SHOWWINDOW = 0x0040;
}
'@
Add-Type -TypeDefinition $csrc -ErrorAction SilentlyContinue

function Get-SimHandle {
    $p = Get-Process | Where-Object { $_.MainWindowTitle -like "*GM ECU Simulator*" -and $_.MainWindowHandle -ne 0 }
    if (-not $p) { throw "GmEcuSimulator.exe is not running" }
    return $p[0].MainWindowHandle
}

function Get-WinRect($hwnd) {
    $r = New-Object WH+RECT
    $ok = [WH]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, 16)
    if ($ok -ne 0) { [WH]::GetWindowRect($hwnd, [ref]$r) | Out-Null }
    return $r
}

function Set-Topmost($hwnd, [bool]$on) {
    $after = [IntPtr]::new($(if ($on) { -1 } else { -2 }))
    $flags = [WH]::NOSIZE -bor [WH]::NOMOVE -bor [WH]::NOACTIVATE -bor [WH]::SHOWWINDOW
    [WH]::SetWindowPos($hwnd, $after, 0,0,0,0, $flags) | Out-Null
}

# ---------------------------------------------------------------------------
# Capture a menu by sending keystrokes while a background job screenshots.
# The job starts its sleep timer BEFORE we touch the window, so the menu
# has been open for (CaptureMs - ~270ms) when the screenshot fires.
# ---------------------------------------------------------------------------
function Capture-Menu($hwnd, [string]$keys, [string]$outPath, [int]$capMs = 750) {
    $r    = Get-WinRect $hwnd
    $L    = $r.L; $T = $r.T
    $capW = ($r.R - $r.L) + 80
    $capH = ($r.B - $r.T) + 300

    $job = Start-Job -ScriptBlock {
        param($l, $t, $cw, $ch, $path, $ms)
        Add-Type -AssemblyName System.Drawing
        Start-Sleep -Milliseconds $ms
        $bmp = New-Object Drawing.Bitmap $cw, $ch
        $g   = [Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($l, $t, 0, 0, (New-Object Drawing.Size $cw, $ch))
        $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
    } -ArgumentList $L, $T, $capW, $capH, $outPath, $capMs

    Start-Sleep -Milliseconds 130
    Set-Topmost $hwnd $true
    [WH]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 160
    [Windows.Forms.SendKeys]::SendWait($keys)

    Receive-Job -Job $job -Wait -AutoRemoveJob | Out-Null

    [Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 100
    Set-Topmost $hwnd $false
    "  Captured: $outPath"
}

# ---------------------------------------------------------------------------
# Capture a modal dialog: open it via keystrokes, screenshot full primary
# display after a delay, then close with Enter.
# ---------------------------------------------------------------------------
function Capture-Dialog($hwnd, [string]$openKeys, [string]$outPath, [int]$capMs = 1600) {
    Set-Topmost $hwnd $true
    [WH]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 160

    $job = Start-Job -ScriptBlock {
        param($path, $ms)
        Add-Type -AssemblyName System.Drawing
        Add-Type -AssemblyName System.Windows.Forms
        Start-Sleep -Milliseconds $ms
        $scr = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object Drawing.Bitmap $scr.Width, $scr.Height
        $g   = [Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen(0, 0, 0, 0, (New-Object Drawing.Size $scr.Width, $scr.Height))
        $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
    } -ArgumentList $outPath, $capMs

    Start-Sleep -Milliseconds 120
    [Windows.Forms.SendKeys]::SendWait($openKeys)

    Receive-Job -Job $job -Wait -AutoRemoveJob | Out-Null
    Start-Sleep -Milliseconds 300

    # Close dialog - try Enter then Escape
    [Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Start-Sleep -Milliseconds 100
    [Windows.Forms.SendKeys]::SendWait("{ESC}")
    Set-Topmost $hwnd $false
    "  Captured: $outPath"
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
Write-Host "=== GM ECU Simulator Manual Build ===" -ForegroundColor Cyan

# 1. J2534 menu (Alt+J)
Write-Host "Capturing J2534 menu..."
$h = Get-SimHandle
Capture-Menu $h "%j" "$shots\06_j2534_menu.png" 750

# 2. Show Registered Devices dialog
#    Menu structure: J2534 -> Register (1st) | Unregister (2nd) | Show registered (3rd)
#    Navigate with DOWN x2 then ENTER.  If the dialog count is different, adjust.
Write-Host "Capturing Show Registered Devices dialog..."
$h = Get-SimHandle
# Alt+J opens menu, DOWN DOWN to "Show registered devices...", ENTER opens dialog
Capture-Dialog $h "%j{DOWN}{DOWN}{ENTER}" "$shots\07_registered_devices_dialog.png" 1600

# 3. Build PDF
Write-Host "Building PDF..."
$py = $null
foreach ($candidate in @('python', 'python3', 'py')) {
    try {
        $ver = & $candidate --version 2>&1
        if ($ver -match 'Python') { $py = $candidate; break }
    } catch { }
}
if (-not $py) { throw "Python not found in PATH. Install Python 3.x and retry." }

& $py "$docs\Build-PDF.py"
if ($LASTEXITCODE -ne 0) { throw "Build-PDF.py exited with code $LASTEXITCODE" }

Write-Host ""
Write-Host "Done. Manual saved to:" -ForegroundColor Green
Write-Host "  $docs\GM_ECU_Simulator_User_Manual.pdf" -ForegroundColor Green
