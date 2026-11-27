param([string]$Out = "$PSScriptRoot\..\app.ico")
# Draws the KilatDesk lightning icon and writes a multi-size .ico (PNG-compressed entries).
Add-Type -AssemblyName System.Drawing
$sizes = 256, 48, 32, 16
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 256.0
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [int](56 * $k); $d = $s - 1
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($d - $r, 0, $r, $r, 270, 90)
    $path.AddArc($d - $r, $d - $r, $r, $r, 0, 90); $path.AddArc(0, $d - $r, $r, $r, 90, 90); $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $s, $s), ([System.Drawing.Color]::FromArgb(255, 20, 60, 160)), ([System.Drawing.Color]::FromArgb(255, 0, 150, 230))
    $g.FillPath($bg, $path)
    $pts = @(@(150, 18), @(56, 146), @(118, 146), @(92, 238), @(200, 100), @(136, 100), @(172, 18)) | ForEach-Object { New-Object System.Drawing.PointF ([float]($_[0] * $k)), ([float]($_[1] * $k)) }
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 214, 0))), [System.Drawing.PointF[]]$pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Host "Icon written: $Out"
