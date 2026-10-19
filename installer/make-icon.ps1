# Draws the TidyWin icon (the same mint-to-violet rounded square with a "T" as the logo in the app's navigation bar)
# and packs it into a multi-size .ico. Run once; the result is committed: src\TidyWin.App\Assets\TidyWin.ico
param([string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\TidyWin.App\Assets\TidyWin.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.Clear([System.Drawing.Color]::Transparent)

  $pad = [math]::Max(0.5, $size * 0.04)
  $d = $size - 2 * $pad
  $r = $d * 0.30
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc($pad, $pad, $r, $r, 180, 90); $path.AddArc($pad + $d - $r, $pad, $r, $r, 270, 90)
  $path.AddArc($pad + $d - $r, $pad + $d - $r, $r, $r, 0, 90); $path.AddArc($pad, $pad + $d - $r, $r, $r, 90, 90)
  $path.CloseFigure()

  $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0x35, 0xF2, 0xB3)), ([System.Drawing.Color]::FromArgb(255, 0x6E, 0x7B, 0xFF)), 45
  $g.FillPath($brush, $path)

  # a soft highlight along the top edge
  $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90
  $g.FillPath($hl, $path)

  $fontName = if ((New-Object System.Drawing.Text.InstalledFontCollection).Families.Name -contains 'Bahnschrift') { 'Bahnschrift' } else { 'Segoe UI' }
  $font = New-Object System.Drawing.Font $fontName, ($size * 0.56), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
  $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
  $text = New-Object System.Drawing.RectangleF 0, ($size * 0.105), $size, $size
  $g.DrawString('T', $font, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x05, 0x10, 0x0C))), $text, $fmt)
  $g.Dispose()
  $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
  $b = New-Frame $s
  $ms = New-Object System.IO.MemoryStream
  $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $b.Dispose()
  , $ms.ToArray()
}

New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]
  $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
  $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Dispose(); $fs.Dispose()

# a 256 px preview next to it, for the README and the repository's social picture
$prev = New-Frame 512
$prev.Save((Join-Path (Split-Path -Parent $Out) 'TidyWin.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$prev.Dispose()
"wrote $Out ($((Get-Item $Out).Length) bytes)"
