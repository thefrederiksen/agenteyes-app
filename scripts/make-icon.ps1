# Generates the AgentEyes app icon: a soft white crescent moon (the quiet shadow)
# on a deep-indigo rounded tile. Output: assets\icon.ico (multi-size) + assets\icon-256.png.
# Repeatable - run again to regenerate after tweaking.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function Draw-Tile([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded indigo tile
    $bg = [System.Drawing.Color]::FromArgb(255, 43, 49, 78)      # deep indigo
    $hi = [System.Drawing.Color]::FromArgb(255, 58, 66, 105)     # lighter top for depth
    $r = [Math]::Max(2, [int]($size * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r*2, $r*2, 180, 90)
    $path.AddArc($size - $r*2, 0, $r*2, $r*2, 270, 90)
    $path.AddArc($size - $r*2, $size - $r*2, $r*2, $r*2, 0, 90)
    $path.AddArc(0, $size - $r*2, $r*2, $r*2, 90, 90)
    $path.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(0, $size)), $hi, $bg)
    $g.FillPath($grad, $path)

    # Crescent moon: full disc minus an offset disc, leaving a soft crescent open to the upper right
    $cx = $size * 0.46; $cy = $size * 0.52; $cr = $size * 0.30
    $moon = New-Object System.Drawing.Drawing2D.GraphicsPath
    $moon.AddEllipse([float]($cx - $cr), [float]($cy - $cr), [float]($cr*2), [float]($cr*2))
    $bite = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bx = $cx + $size * 0.14; $by = $cy - $size * 0.12; $br = $cr * 0.92
    $bite.AddEllipse([float]($bx - $br), [float]($by - $br), [float]($br*2), [float]($br*2))
    $region = New-Object System.Drawing.Region($moon)
    $region.Exclude($bite)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(245, 248, 248, 252))
    $g.FillRegion($white, $region)

    # One small star, upper right, for the friendly touch
    $sx = $size * 0.70; $sy = $size * 0.28; $sr = [Math]::Max(1.0, $size * 0.045)
    $g.FillEllipse($white, [float]($sx - $sr), [float]($sy - $sr), [float]($sr*2), [float]($sr*2))

    $g.Dispose()
    return $bmp
}

# PNG previews + ICO container (PNG-compressed entries, supported since Vista)
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @{}
foreach ($s in $sizes) {
    $bmp = Draw-Tile $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$s] = $ms.ToArray()
    if ($s -eq 256) { [IO.File]::WriteAllBytes((Join-Path $assets 'icon-256.png'), $ms.ToArray()) }
    $bmp.Dispose(); $ms.Dispose()
}

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)   # ICONDIR
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $data = $pngs[$s]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))   # width (0 = 256)
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))   # height
    $w.Write([byte]0); $w.Write([byte]0)                      # palette, reserved
    $w.Write([uint16]1); $w.Write([uint16]32)                 # planes, bpp
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset) # size, offset
    $offset += $data.Length
}
foreach ($s in $sizes) { $w.Write($pngs[$s]) }
[IO.File]::WriteAllBytes((Join-Path $assets 'icon.ico'), $ico.ToArray())
$w.Dispose(); $ico.Dispose()
"icon written: $(Join-Path $assets 'icon.ico') ($((Get-Item (Join-Path $assets 'icon.ico')).Length) bytes)"