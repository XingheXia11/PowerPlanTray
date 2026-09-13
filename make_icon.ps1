# Generate app.ico (multi-size) and assets/icon-{blue,green,red}.png
# iOS design language: full-bleed squircle (superellipse n=5) + soft vertical
#                      gradient + rounded battery glyph with subtle shadow.
#                      No borders, no gloss.
Add-Type -AssemblyName System.Drawing

function Add-RoundedRect([System.Drawing.Drawing2D.GraphicsPath]$path, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc(($x + $w - $d), $y, $d, $d, 270, 90)
    $path.AddArc(($x + $w - $d), ($y + $h - $d), $d, $d, 0, 90)
    $path.AddArc($x, ($y + $h - $d), $d, $d, 90, 90)
    $path.CloseFigure()
}

function Add-Squircle([System.Drawing.Drawing2D.GraphicsPath]$path, [float]$x, [float]$y, [float]$w, [float]$h) {
    $steps = 128
    $n = 5.0
    $p = 2.0 / $n
    $cx = $x + $w / 2.0
    $cy = $y + $h / 2.0
    $a = $w / 2.0
    $b = $h / 2.0
    $pts = New-Object 'System.Drawing.PointF[]' $steps
    for ($i = 0; $i -lt $steps; $i++) {
        $t = 2.0 * [Math]::PI * $i / $steps
        $ct = [Math]::Cos($t)
        $st = [Math]::Sin($t)
        $pts[$i] = New-Object System.Drawing.PointF (
            [float]($cx + $a * [Math]::Sign($ct) * [Math]::Pow([Math]::Abs($ct), $p)),
            [float]($cy + $b * [Math]::Sign($st) * [Math]::Pow([Math]::Abs($st), $p)))
    }
    $path.AddPolygon($pts)
}

function New-Color([int[]]$c) { [System.Drawing.Color]::FromArgb(255, $c[0], $c[1], $c[2]) }

$Palettes = @(
    @{ Name = 'green'; Top = @(123, 223, 159); Mid = @(60, 190, 121);  Bottom = @(35, 164, 92);  Inner = @(28, 150, 80) },  # 电影模式
    @{ Name = 'blue';  Top = @(90, 167, 255);  Mid = @(47, 142, 240);  Bottom = @(21, 101, 192); Inner = @(23, 116, 200) },  # AI模式
    @{ Name = 'red';   Top = @(255, 123, 114); Mid = @(239, 77, 77);   Bottom = @(198, 40, 40);  Inner = @(200, 55, 55) }   # 游戏模式
)

function Draw-IconBitmap([int]$size, [hashtable]$p) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $s = $size / 256.0

    # full-bleed squircle + soft vertical 3-stop gradient
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-Squircle $tile (2 * $s) (2 * $s) (252 * $s) (252 * $s)
    $gradRect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $gradRect, ([System.Drawing.Color]::Black), ([System.Drawing.Color]::Black), 90.0
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = [System.Drawing.Color[]]@((New-Color $p.Top), (New-Color $p.Mid), (New-Color $p.Bottom))
    $blend.Positions = [float[]]@(0.0, 0.5, 1.0)
    $grad.InterpolationColors = $blend
    $g.FillPath($grad, $tile)

    # battery subtle shadow (offset +7.2 in 256-space)
    $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(55, 0, 0, 0))
    $bodySh = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundedRect $bodySh (44 * $s) (87.2 * $s) (132 * $s) (96 * $s) (24 * $s)
    $g.FillPath($shadow, $bodySh)
    $capSh = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundedRect $capSh (186 * $s) (111.2 * $s) (26 * $s) (48 * $s) (12 * $s)
    $g.FillPath($shadow, $capSh)

    # white battery: body (44,80,132,96 r24) + cap (186,104,26,48 r12)
    $white = [System.Drawing.Brushes]::White
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundedRect $body (44 * $s) (80 * $s) (132 * $s) (96 * $s) (24 * $s)
    $g.FillPath($white, $body)
    $cap = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundedRect $cap (186 * $s) (104 * $s) (26 * $s) (48 * $s) (12 * $s)
    $g.FillPath($white, $cap)

    # bolt cutout inside the body
    $bxs = @(125, 83, 105, 97, 137, 115)
    $bys = @(98, 142, 142, 160, 118, 118)
    $pts = New-Object 'System.Drawing.PointF[]' 6
    for ($i = 0; $i -lt 6; $i++) {
        $pts[$i] = New-Object System.Drawing.PointF (($bxs[$i] * $s), ($bys[$i] * $s))
    }
    $innerCol = New-Color $p.Inner
    $boltFill = New-Object System.Drawing.SolidBrush $innerCol
    $g.FillPolygon($boltFill, $pts)
    $rounder = New-Object System.Drawing.Pen $innerCol, (8.8 * $s)
    $rounder.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPolygon($rounder, $pts)

    $g.Dispose()
    return $bmp
}

function Get-BmpEntry([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $row = New-Object byte[] ($w * 4)
    $maskRow = [int][Math]::Ceiling(([Math]::Ceiling($w / 8.0)) / 4.0) * 4
    $mask = New-Object byte[] ($maskRow * $h)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]0); $bw.Write([uint32](($w * $h * 4) + $mask.Length))
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $stride), $row, 0, $w * 4)
        $bw.Write($row)
    }
    $bw.Write($mask)
    $bw.Flush()
    $bmp.UnlockBits($data)
    return ,$ms.ToArray()
}

$toolDir = [IO.Path]::Combine([Environment]::GetFolderPath('Desktop'), 'PowerPlanTray')
$icoPath = [IO.Path]::Combine($toolDir, 'app.ico')
$blue = $Palettes[1]

# ---- app.ico: sizes 16-128 as raw BGRA, 256 as embedded PNG ----
$entries = @()
foreach ($sz in @(16, 24, 32, 48, 64, 128, 256)) {
    $bmp = Draw-IconBitmap $sz $blue
    if ($sz -le 128) {
        $dataBytes = Get-BmpEntry $bmp
    } else {
        $msPNG = New-Object System.IO.MemoryStream
        $bmp.Save($msPNG, [System.Drawing.Imaging.ImageFormat]::Png)
        $dataBytes = $msPNG.ToArray()
        $msPNG.Dispose()
    }
    $bmp.Dispose()
    $entries += ,@($sz, $dataBytes)
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = $(if ($e[0] -ge 256) { 0 } else { $e[0] })
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$e[1].Length); $bw.Write([uint32]$offset)
    $offset += $e[1].Length
}
foreach ($e in $entries) { $bw.Write($e[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Dispose()

# ---- assets: one 256px PNG per palette (for README) ----
$assetsDir = [IO.Path]::Combine($toolDir, 'assets')
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
foreach ($p in $Palettes) {
    $bmp = Draw-IconBitmap 256 $p
    $bmp.Save((Join-Path $assetsDir ('icon-' + $p.Name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

Write-Host 'app.ico + assets generated (iOS style)'
