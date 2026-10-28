# Draws the RegWatch icon (a page under a magnifier with a red score dot) and writes
# src\RegWatch.App\Assets\RegWatch.ico (16-256 px) plus RegWatch-256.png.
# Run with Windows PowerShell:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$outDir = Join-Path $PSScriptRoot '..\src\RegWatch.App\Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null

function Brush([string]$hex) {
    $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex))
    $b.Freeze(); $b
}
function Pen($brush, [double]$width) {
    $p = New-Object System.Windows.Media.Pen($brush, $width)
    $p.StartLineCap = 'Round'; $p.EndLineCap = 'Round'; $p.LineJoin = 'Round'
    $p
}
function Pt([double]$x, [double]$y) { New-Object System.Windows.Point($x, $y) }
function Poly([double[]]$xy, [bool]$closed) {
    $g = New-Object System.Windows.Media.StreamGeometry
    $ctx = $g.Open()
    $ctx.BeginFigure((Pt $xy[0] $xy[1]), $true, $closed)
    for ($i = 2; $i -lt $xy.Length; $i += 2) { $ctx.LineTo((Pt $xy[$i] $xy[$i + 1]), $true, $true) }
    $ctx.Close(); $g.Freeze(); $g
}

$navy  = Brush '#1F3A5F'
$paper = Brush '#FFFFFF'
$fold  = Brush '#D5DEE8'
$lines = Brush '#9AA7B8'
$lens  = Brush '#DCEBFA'
$red   = Brush '#D64545'

# Everything is drawn on a 256-unit canvas; small sizes get thicker strokes and fewer details.
function Render([int]$size) {
    $k = if ($size -le 24) { 1.7 } elseif ($size -le 48) { 1.25 } else { 1.0 }
    $v = New-Object System.Windows.Media.DrawingVisual
    $dc = $v.RenderOpen()
    $scale = $size / 256.0
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))

    $dc.DrawGeometry($paper, (Pen $navy (10 * $k)), (Poly @(36,22, 138,22, 178,62, 178,214, 36,214) $true))
    $dc.DrawGeometry($fold, (Pen $navy (8 * $k)), (Poly @(138,22, 138,62, 178,62) $true))
    if ($size -gt 24) {
        foreach ($y in 92, 120, 148) { $dc.DrawLine((Pen $lines 10), (Pt 62 $y), (Pt 118 $y)) }
    }

    $dc.DrawLine((Pen $navy (26 * [Math]::Min($k, 1.3))), (Pt 196 196), (Pt 234 234))
    $dc.DrawEllipse($lens, (Pen $navy (14 * $k)), (Pt 162 162), 52, 52)
    $dc.DrawEllipse($red, $null, (Pt 162 162), (16 * [Math]::Min($k, 1.3)), (16 * [Math]::Min($k, 1.3)))

    $dc.Pop(); $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($v)
    # Icons store straight (not premultiplied) alpha.
    New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($bmp, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
}

function PngBytes($bitmap) {
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms); $ms.ToArray()
}

# Classic 32-bit DIB entry (BITMAPINFOHEADER, bottom-up rows, then a 1-bit AND mask).
function DibBytes($bitmap, [int]$size) {
    $stride = $size * 4
    $px = New-Object byte[] ($stride * $size)
    $bitmap.CopyPixels($px, $stride, 0)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]($stride * $size + $maskStride * $size)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($px, $y * $stride, $stride) }
    $w.Write((New-Object byte[] ($maskStride * $size)))
    $w.Flush(); $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = Render $s
    if ($s -eq 256) { [System.IO.File]::WriteAllBytes((Join-Path $outDir 'RegWatch-256.png'), (PngBytes $bmp)) }
    , @($s, $(if ($s -eq 256) { PngBytes $bmp } else { DibBytes $bmp $s }))
}

$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ms)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img[0] -ge 256) { 0 } else { $img[0] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$img[1].Length); $w.Write([int]$offset)
    $offset += $img[1].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'RegWatch.ico'), $ms.ToArray())
Write-Output "Wrote $(Join-Path $outDir 'RegWatch.ico') ($($images.Count) sizes)"
