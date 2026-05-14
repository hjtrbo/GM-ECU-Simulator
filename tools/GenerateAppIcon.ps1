# Generates the GM ECU Simulator taskbar/title-bar icon.
#
# Design: dark rounded-square badge. Small "GM" pill in the top-left
# (the family marker - GM Heritage Blue #2D80E6 fill, white text) and
# a large bold "SIM" wordmark dominating the centre. Sibling GM tools
# reuse the same shape and pill - only the centre wordmark changes
# (e.g. "LOG", "SEC", "TEST"...).
#
# Output: a single multi-resolution .ico (16/24/32/48/64/128/256), built
# in PNG-in-ICO form. No external tools needed - pure System.Drawing.
#
# Run from the repo root:
#   powershell -ExecutionPolicy Bypass -File tools\GenerateAppIcon.ps1

Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath {
    param([float]$x, [float]$y, [float]$w, [float]$h, [float]$r)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x,            $y,            $d, $d, 180, 90)
    $path.AddArc($x + $w - $d,  $y,            $d, $d, 270, 90)
    $path.AddArc($x + $w - $d,  $y + $h - $d,  $d, $d,   0, 90)
    $path.AddArc($x,            $y + $h - $d,  $d, $d,  90, 90)
    $path.CloseFigure()
    return $path
}

function Render-Icon {
    param([int]$size)

    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint  = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $s = $size / 256.0

    # Palette pulled from the Midnight theme so the icon visually matches
    # the app chrome out of the box.
    $bg     = [System.Drawing.Color]::FromArgb(0xFF, 0x14, 0x17, 0x1F)   # Bg.Base
    $text   = [System.Drawing.Color]::FromArgb(0xFF, 0xE6, 0xE9, 0xF2)   # Text.Primary
    $accent = [System.Drawing.Color]::FromArgb(0xFF, 0x2D, 0x80, 0xE6)   # GM Heritage Blue
    $white  = [System.Drawing.Color]::White

    # 1) Badge background (rounded square covering the whole canvas).
    $bgPath = New-RoundedRectPath 0 0 $size $size ([float](32 * $s))
    $bgBrush = New-Object System.Drawing.SolidBrush $bg
    $g.FillPath($bgBrush, $bgPath)

    # 2) GM family pill, top-left. Only at >= 64 px - smaller than that
    #    the pill becomes a few pixels and just clutters the wordmark.
    #    Taskbar / alt-tab will use 24-32 px in most cases, so for those
    #    the wordmark gets the whole canvas instead.
    $showPill = $size -ge 64
    $accentBrush = New-Object System.Drawing.SolidBrush $accent
    if ($showPill) {
        $pillW    = [single](96 * $s)
        $pillH    = [single](46 * $s)
        $pillX    = [single](20 * $s)
        $pillY    = [single](20 * $s)
        $pillPath = New-RoundedRectPath $pillX $pillY $pillW $pillH ([float](10 * $s))
        $g.FillPath($accentBrush, $pillPath)

        $pillFontSize = [single](30 * $s)
        $pillFont = New-Object System.Drawing.Font("Segoe UI", $pillFontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $pillBrush = New-Object System.Drawing.SolidBrush $white
        $pillSf = New-Object System.Drawing.StringFormat
        $pillSf.Alignment     = [System.Drawing.StringAlignment]::Center
        $pillSf.LineAlignment = [System.Drawing.StringAlignment]::Center
        $pillSf.FormatFlags   = [System.Drawing.StringFormatFlags]::NoWrap
        $pillRect = New-Object System.Drawing.RectangleF $pillX, $pillY, $pillW, $pillH
        $g.DrawString("GM", $pillFont, $pillBrush, $pillRect, $pillSf)
        $pillFont.Dispose()
        $pillBrush.Dispose()
    }

    # 3) "SIM" wordmark - dominates the canvas. Initial font size targets
    #    ~95 % of the available horizontal space; we measure the rendered
    #    width and shrink iteratively if it overruns the canvas (Segoe UI
    #    Bold glyph metrics aren't exactly proportional at small pixel
    #    sizes thanks to hinting). Centred manually with DrawString(PointF)
    #    so the bitmap is the only clip boundary - rect-based DrawString
    #    silently clips overruns and produced the "SI" / wrapping bug.
    if ($showPill) {
        $simFontSize    = [single](110 * $s)
        $simCentreY     = [single](56 * $s) + ($size - [single](56 * $s)) / 2.0
        $maxWidth       = $size * 0.86
    } else {
        $simFontSize    = [single]($size * 0.62)
        $simCentreY     = [single]$size / 2.0
        $maxWidth       = $size * 0.92
    }

    $simBrush = New-Object System.Drawing.SolidBrush $text
    $sf = New-Object System.Drawing.StringFormat ([System.Drawing.StringFormat]::GenericTypographic)
    $sf.FormatFlags   = [System.Drawing.StringFormatFlags]::NoWrap

    # Iteratively shrink until the measured glyph run fits inside maxWidth.
    $simFont = $null
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        if ($simFont -ne $null) { $simFont.Dispose() }
        $simFont = New-Object System.Drawing.Font("Segoe UI", $simFontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $measured = $g.MeasureString("SIM", $simFont, [int]([math]::Ceiling($size * 2)), $sf)
        if ($measured.Width -le $maxWidth) { break }
        $simFontSize = $simFontSize * 0.92
    }

    $tx = ($size - $measured.Width) / 2.0
    $ty = $simCentreY - $measured.Height / 2.0
    $g.DrawString("SIM", $simFont, $simBrush, [single]$tx, [single]$ty, $sf)

    $simFont.Dispose()
    $simBrush.Dispose()
    $bgBrush.Dispose()
    $accentBrush.Dispose()
    $g.Dispose()
    return $bmp
}

# Sizes to embed in the .ico. 16-256 covers all Windows shell contexts.
$sizes = 16, 24, 32, 48, 64, 128, 256

$outDir = Join-Path $PSScriptRoot "..\GmEcuSimulator\Resources"
$outIco = Join-Path $outDir "app.ico"
$outPreview = Join-Path $outDir "app-256-preview.png"

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# Render and PNG-encode every size.
$pngs = @{}
foreach ($size in $sizes) {
    $bmp = Render-Icon $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$size] = $ms.ToArray()
    if ($size -eq 256) { $bmp.Save($outPreview, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    $ms.Dispose()
    Write-Host ("  {0,3}x{0,-3}  -> {1,6} bytes PNG" -f $size, $pngs[$size].Length)
}

# Assemble multi-image ICO. Header (6) + N * directory entry (16) + concatenated PNG payloads.
$mem = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $mem

$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)

$dataOffset = 6 + ($sizes.Count * 16)
foreach ($size in $sizes) {
    $byteLen = $pngs[$size].Length
    $wh = if ($size -ge 256) { 0 } else { $size }
    $writer.Write([byte]$wh)
    $writer.Write([byte]$wh)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$byteLen)
    $writer.Write([uint32]$dataOffset)
    $dataOffset += $byteLen
}

foreach ($size in $sizes) { $writer.Write($pngs[$size]) }

[System.IO.File]::WriteAllBytes($outIco, $mem.ToArray())
$writer.Dispose()
$mem.Dispose()

Write-Host ""
Write-Host "Wrote $outIco ($([System.IO.File]::ReadAllBytes($outIco).Length) bytes)"
Write-Host "Preview $outPreview"
