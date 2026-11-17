# Draws the app icon (white battery on black) and writes src\icon.ico with PNG frames.
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$sizes = 16, 24, 32, 48, 64, 256
$frames = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([Drawing.Color]::Transparent)
    $k = $s / 32.0
    $bg = New-Object Drawing.Drawing2D.GraphicsPath
    $r = 7 * $k; $w = $s - 1
    $bg.AddArc(0, 0, $r, $r, 180, 90); $bg.AddArc($w - $r, 0, $r, $r, 270, 90)
    $bg.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $bg.AddArc(0, $w - $r, $r, $r, 90, 90); $bg.CloseFigure()
    $g.FillPath([Drawing.Brushes]::Black, $bg)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), ([Math]::Max(1, 2 * $k))
    $g.DrawRectangle($pen, [float](5 * $k), [float](10 * $k), [float](19 * $k), [float](12 * $k))
    $g.FillRectangle([Drawing.Brushes]::White, [float](25 * $k), [float](13.5 * $k), [float](2.5 * $k), [float](5 * $k))
    $g.FillRectangle([Drawing.Brushes]::White, [float](8 * $k), [float](13 * $k), [float](10 * $k), [float](6 * $k))
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$out = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $frames[$i].Length
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $bw.Write($f) }
[IO.File]::WriteAllBytes("$root\src\icon.ico", $out.ToArray())
"wrote src\icon.ico"
