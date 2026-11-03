#Requires -Version 7
<#
.SYNOPSIS
Builds the app and extension icons from the logo.

.DESCRIPTION
Crops the logo in build/logo/logo-source.png to the mark plus a small margin, places it on a rounded tile in
the logo's own background color (so the dark mark stays visible on a dark taskbar), and writes:

- src/Assistant.App/assistant.ico: the exe, tray, window, and installer icon, at 16 to 256 pixels.
- extension/icon.png: the desktop extension icon, 256 pixels.

And without the background:
- src/Assistant.Web/wwwroot/logo.png: the logo at the top of the app's sidebar.
- src/Assistant.App/assistant-small.ico: the title-bar icon.

Run it again after replacing logo-source.png.
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot 'logo\logo-source.png'),

    # Margin around the mark, as a share of the tile.
    [double]$Margin = 0.09
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot

# Removing the background, in C# because it touches every pixel. The logo is two flat grays on a light
# background: dark outer shapes and a lighter star in the middle. Each pixel's alpha comes from how far it is
# from the background toward its ink, which keeps the anti-aliased edges smooth and the star solid.
if (-not ('LogoCutout' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing.Common, System.Drawing.Primitives, System.Collections, System.Runtime.InteropServices -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class LogoCutout
{
    public static Bitmap Cut(Bitmap src, int background)
    {
        int w = src.Width, h = src.Height;
        var px = Read(src);
        var lum = new int[w * h];
        int darkest = 255, minX = w, minY = h, maxX = 0, maxY = 0;
        for (int i = 0; i < lum.Length; i++)
        {
            lum[i] = (px[i * 4] + px[i * 4 + 1] + px[i * 4 + 2]) / 3;
            darkest = Math.Min(darkest, lum[i]);
            if (lum[i] < background - 40)
            {
                int x = i % w, y = i / w;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }

        // The star is the shape at the center; it doesn't touch the outer shapes, so a flood fill finds it.
        int center = (minY + maxY) / 2 * w + (minX + maxX) / 2;
        int starInk = lum[center];
        var star = new bool[w * h];
        var queue = new Queue<int>();
        queue.Enqueue(center);
        star[center] = true;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(), x = i % w, y = i / w;
            foreach (int n in new[] { x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, y > 0 ? i - w : -1, y < h - 1 ? i + w : -1 })
            {
                if (n >= 0 && !star[n] && lum[n] < background - 12)
                {
                    star[n] = true;
                    queue.Enqueue(n);
                }
            }
        }

        var output = new byte[px.Length];
        for (int i = 0; i < lum.Length; i++)
        {
            int ink = star[i] ? starInk : darkest;
            double alpha = Math.Clamp((background - lum[i]) / (double)(background - ink), 0, 1);
            output[i * 4] = output[i * 4 + 1] = output[i * 4 + 2] = (byte)ink;
            output[i * 4 + 3] = (byte)Math.Round(alpha * 255);
        }

        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(output, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        result.UnlockBits(data);
        return result;
    }

    private static byte[] Read(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * w * 4, w * 4);
        src.UnlockBits(data);
        return px;
    }
}
'@
}

$src = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source))
try {
    # The background is the corner color. The mark is anything clearly darker.
    $bg = $src.GetPixel(4, 4)
    $bgLum = ($bg.R + $bg.G + $bg.B) / 3
    $minX = $src.Width; $minY = $src.Height; $maxX = 0; $maxY = 0
    for ($y = 0; $y -lt $src.Height; $y += 2) {
        for ($x = 0; $x -lt $src.Width; $x += 2) {
            $p = $src.GetPixel($x, $y)
            if (($p.R + $p.G + $p.B) / 3 -lt $bgLum - 40) {
                if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -le $minX) { throw 'No logo mark found in the source image.' }

    # A square around the mark's center, with the margin added.
    $side = [Math]::Max($maxX - $minX, $maxY - $minY) / (1 - 2 * $Margin)
    $cx = ($minX + $maxX) / 2; $cy = ($minY + $maxY) / 2
    $crop = [System.Drawing.RectangleF]::new($cx - $side / 2, $cy - $side / 2, $side, $side)
    Write-Host ("Mark at {0},{1} to {2},{3}; tile {4:0}px" -f $minX, $minY, $maxX, $maxY, $side)

    function New-Tile([int]$size) {
        $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
        $g.Clear([System.Drawing.Color]::Transparent)

        # Rounded tile: about 18% corner radius, like Windows app icons.
        $r = [Math]::Max(2, $size * 0.18); $d = $r * 2; $s = $size - 1
        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($s - $d, 0, $d, $d, 270, 90)
        $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90); $path.AddArc(0, $s - $d, $d, $d, 90, 90); $path.CloseFigure()
        $g.SetClip($path)
        $g.DrawImage($src, [System.Drawing.RectangleF]::new(0, 0, $size, $size), $crop, [System.Drawing.GraphicsUnit]::Pixel)
        $g.Dispose(); $path.Dispose()
        return $bmp
    }

    # The mark alone, cropped like the tile, on a transparent background.
    function New-Cutout($cut, [int]$size) {
        $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
        $g.CompositingMode = 'SourceOver'; $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($cut, [System.Drawing.RectangleF]::new(0, 0, $size, $size), $crop, [System.Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()
        return $bmp
    }

    function Get-PngBytes($bmp) {
        $ms = [IO.MemoryStream]::new()
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $ms.ToArray()
    }

    # Classic 32-bit bitmap entry: header, bottom-up BGRA rows, then an all-zero AND mask (alpha does the masking).
    function Get-DibBytes($bmp) {
        $n = $bmp.Width
        $ms = [IO.MemoryStream]::new()
        $bw = [IO.BinaryWriter]::new($ms)
        $bw.Write([UInt32]40); $bw.Write([Int32]$n); $bw.Write([Int32]($n * 2)); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]0); $bw.Write([UInt32]0); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($y = $n - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $n; $x++) {
                $p = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$p.B); $bw.Write([byte]$p.G); $bw.Write([byte]$p.R); $bw.Write([byte]$p.A)
            }
        }
        $bw.Write([byte[]]::new([Math]::Ceiling($n / 32) * 4 * $n))
        $bw.Flush()
        return , $ms.ToArray()
    }

    # Bitmap entries for the small sizes, which every reader handles, and PNG for 256 to keep the file small.
    function Write-Ico([string]$path, [int[]]$sizes, [scriptblock]$render) {
    $images = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $b = & $render $size
        $images.Add([byte[]]$(if ($size -ge 256) { Get-PngBytes $b } else { Get-DibBytes $b }))
        $b.Dispose()
    }
    $ico = [IO.MemoryStream]::new()
    $w = [IO.BinaryWriter]::new($ico)
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $w.Write($img) }
    $w.Flush()
    [IO.File]::WriteAllBytes($path, $ico.ToArray())
    }

    Write-Ico (Join-Path $root 'src\Assistant.App\assistant.ico') (16, 20, 24, 32, 40, 48, 64, 128, 256) { param($s) New-Tile $s }

    $ext = New-Tile 256
    $ext.Save((Join-Path $root 'extension\icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $ext.Dispose()

    # Without the background: the sidebar logo (shown at 26 px; 128 keeps it sharp on high-DPI screens) and the
    # small title-bar icon. The taskbar keeps the tile.
    $cutout = [LogoCutout]::Cut($src, [int]$bgLum)
    try {
        $web = New-Cutout $cutout 128
        $web.Save((Join-Path $root 'src\Assistant.Web\wwwroot\logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        $web.Dispose()
        Write-Ico (Join-Path $root 'src\Assistant.App\assistant-small.ico') (16, 20, 24, 32, 40, 48) { param($s) New-Cutout $cutout $s }
    }
    finally {
        $cutout.Dispose()
    }

    Write-Host 'Wrote assistant.ico, assistant-small.ico, extension/icon.png, and the sidebar logo.png.'
}
finally {
    $src.Dispose()
}
