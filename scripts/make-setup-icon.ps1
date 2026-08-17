# Generates the SETUP icon - deliberately distinct from the app icon (assets\icon.ico):
# same deep-indigo tile for family identity, but a green download-arrow dropping into a
# white install tray (the universal "installer" glyph) instead of the crescent moon.
# Output: assets\setup-icon.ico (multi-size) + assets\setup-icon-256.png. Repeatable.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function Draw-Tile([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded indigo tile (same palette as the app icon for family identity)
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

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(245, 248, 248, 252))
    $green = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 34, 197, 94))   # the wizard's success green

    # Install tray: a white U (open at the top) along the bottom
    $stroke = [Math]::Max(1.5, $size * 0.065)
    $tL = $size * 0.22; $tR = $size * 0.78; $tT = $size * 0.56; $tB = $size * 0.80
    $g.FillRectangle($white, [float]$tL, [float]$tT, [float]$stroke, [float]($tB - $tT))                       # left wall
    $g.FillRectangle($white, [float]($tR - $stroke), [float]$tT, [float]$stroke, [float]($tB - $tT))           # right wall
    $g.FillRectangle($white, [float]$tL, [float]($tB - $stroke), [float]($tR - $tL), [float]$stroke)           # floor

    # Green download arrow dropping into the tray
    $cx = $size * 0.50
    $shaftW = $size * 0.13
    $shaftTop = $size * 0.16
    $headTop = $size * 0.44
    $headW = $size * 0.34
    $tip = $size * 0.66
    $g.FillRectangle($green, [float]($cx - $shaftW/2), [float]$shaftTop, [float]$shaftW, [float]($headTop - $shaftTop + 1))
    $head = New-Object System.Drawing.Drawing2D.GraphicsPath
    $head.AddPolygon(@(
        (New-Object System.Drawing.PointF([float]($cx - $headW/2), [float]$headTop)),
        (New-Object System.Drawing.PointF([float]($cx + $headW/2), [float]$headTop)),
        (New-Object System.Drawing.PointF([float]$cx, [float]$tip))
    ))
    $g.FillPath($green, $head)

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
    if ($s -eq 256) { [IO.File]::WriteAllBytes((Join-Path $assets 'setup-icon-256.png'), $ms.ToArray()) }
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
[IO.File]::WriteAllBytes((Join-Path $assets 'setup-icon.ico'), $ico.ToArray())
$w.Dispose(); $ico.Dispose()
"setup icon written: $(Join-Path $assets 'setup-icon.ico') ($((Get-Item (Join-Path $assets 'setup-icon.ico')).Length) bytes)"
