param(
    [Parameter(Mandatory=$true)][string]$Title,
    [Parameter(Mandatory=$true)][string]$Out,
    [int]$DelayMs = 450,
    [switch]$ScreenCopy
)

Add-Type -AssemblyName System.Drawing

$src = @'
using System;
using System.Runtime.InteropServices;
using System.Drawing;
public class Win {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] public static extern bool LockSetForegroundWindow(uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
    public struct RECT { public int L,T,R,B; }
}
'@
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Drawing -ErrorAction SilentlyContinue

$procs = Get-Process | Where-Object { $_.MainWindowTitle -like "*$Title*" -and $_.MainWindowHandle -ne 0 }
if (-not $procs) { throw "No window matching '*$Title*'" }
$h = $procs[0].MainWindowHandle

Start-Sleep -Milliseconds $DelayMs

$r = New-Object Win+RECT
$ok = [Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
if ($ok -ne 0) { [Win]::GetWindowRect($h, [ref]$r) | Out-Null }

$w = $r.R - $r.L
$ht = $r.B - $r.T
$bmp = New-Object Drawing.Bitmap $w, $ht
$g = [Drawing.Graphics]::FromImage($bmp)
if ($ScreenCopy) {
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object Drawing.Size $w, $ht))
} else {
    $hdc = $g.GetHdc()
    [Win]::PrintWindow($h, $hdc, 0x2) | Out-Null
    $g.ReleaseHdc($hdc)
}
$bmp.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"Saved $Out ($w x $ht) -- $($procs[0].MainWindowTitle)"
