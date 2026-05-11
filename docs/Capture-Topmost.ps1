param(
    [Parameter(Mandatory=$true)][string]$Title,
    [Parameter(Mandatory=$true)][string]$Out,
    [int]$DelayMs = 350
)

Add-Type -AssemblyName System.Drawing

$src = @'
using System;
using System.Runtime.InteropServices;
public class WT {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int h_, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
    public struct RECT { public int L,T,R,B; }
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
}
'@
Add-Type -TypeDefinition $src -ErrorAction SilentlyContinue

$procs = Get-Process | Where-Object { $_.MainWindowTitle -like "*$Title*" -and $_.MainWindowHandle -ne 0 }
if (-not $procs) { throw "No window matching '*$Title*'" }
$h = $procs[0].MainWindowHandle

# Make topmost briefly so popups render above all other windows
$flags = [WT]::SWP_NOMOVE -bor [WT]::SWP_NOSIZE -bor [WT]::SWP_NOACTIVATE -bor [WT]::SWP_SHOWWINDOW
[WT]::SetWindowPos($h, [WT]::HWND_TOPMOST, 0, 0, 0, 0, $flags) | Out-Null
Start-Sleep -Milliseconds $DelayMs

$r = New-Object WT+RECT
$ok = [WT]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
if ($ok -ne 0) { [WT]::GetWindowRect($h, [ref]$r) | Out-Null }

$w = $r.R - $r.L
$ht = $r.B - $r.T

# Include some padding to the right/below to catch popup menus that may extend past the window rect
$padW = 40
$padH = 80
$bmp = New-Object Drawing.Bitmap ($w + $padW), ($ht + $padH)
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object Drawing.Size ($w + $padW), ($ht + $padH)))

# Restore non-topmost
[WT]::SetWindowPos($h, [WT]::HWND_NOTOPMOST, 0, 0, 0, 0, $flags) | Out-Null

$bmp.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"Saved $Out ($($w + $padW) x $($ht + $padH))"
