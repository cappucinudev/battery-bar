# Builds src\icon.ico (PNG frames, 16-256 px, rounded corners) from src\icon-source.png.
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$src = [Drawing.Image]::FromFile("$root\src\icon-source.png")
$side = [Math]::Min($src.Width, $src.Height)
$crop = New-Object Drawing.Rectangle ([int](($src.Width - $side) / 2)), ([int](($src.Height - $side) / 2)), $side, $side
$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)
    $r = [Math]::Max(2, $s * 0.18); $w = $s; $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90); $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90); $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90); $path.CloseFigure()
    $g.SetClip($path)
    $g.DrawImage($src, (New-Object Drawing.Rectangle 0, 0, $s, $s), $crop, [Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}
$src.Dispose()
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
