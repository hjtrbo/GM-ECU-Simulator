param(
    [Parameter(Mandatory=$true)][string]$Title,
    [Parameter(Mandatory=$true)][string]$Out,
    [Parameter(Mandatory=$true)][string]$MenuKeys,   # e.g. "%f" for Alt+F
    [int]$CaptureDelayMs = 700   # ms after SendKeys fires that the job screenshots
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$src = @'
using System;
using System.Runtime.InteropServices;
public class CM {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int h_, uint f);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
    public struct RECT { public int L,T,R,B; }
}
'@
Add-Type -TypeDefinition $src -ErrorAction SilentlyContinue

$procs = Get-Process | Where-Object { $_.MainWindowTitle -like "*$Title*" -and $_.MainWindowHandle -ne 0 }
if (-not $procs) { throw "No window matching '*$Title*'" }
$h = $procs[0].MainWindowHandle

# Get window rect before touching focus
$r = New-Object CM+RECT
$ok = [CM]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
if ($ok -ne 0) { [CM]::GetWindowRect($h, [ref]$r) | Out-Null }
$L = $r.L; $T = $r.T
$W = $r.R - $r.L; $H = $r.B - $r.T
$capW = $W + 60
$capH = $H + 280

# Launch a background job that will screenshot after a delay.
# The job fires its timer from NOW, so by the time $CaptureDelayMs elapses
# the main thread has already sent keys and the menu is open.
$job = Start-Job -ScriptBlock {
    param($l, $t, $cw, $ch, $out, $ms)
    Add-Type -AssemblyName System.Drawing
    Start-Sleep -Milliseconds $ms
    $bmp = New-Object Drawing.Bitmap $cw, $ch
    $g   = [Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($l, $t, 0, 0, (New-Object Drawing.Size $cw, $ch))
    $bmp.Save($out, [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "Saved $out ($cw x $ch)"
} -ArgumentList $L, $T, $capW, $capH, $Out, $CaptureDelayMs

# Give job a moment to start and enter its sleep
Start-Sleep -Milliseconds 120

# Bring window forward and open the menu
[CM]::SetWindowPos($h, [IntPtr]::new(-1), 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
[CM]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 150
[Windows.Forms.SendKeys]::SendWait($MenuKeys)

# Stay idle so the WPF app keeps focus while the background job captures
$result = Receive-Job -Job $job -Wait -AutoRemoveJob
Write-Output $result

# Close menu and restore non-topmost
[Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Milliseconds 80
[CM]::SetWindowPos($h, [IntPtr]::new(-2), 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
