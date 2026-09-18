param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Package the existing Dock artwork; no crop, recoloring or background changes.
$sourcePath = Join-Path $PSScriptRoot 'Vellume.App\Assets\Brand\vellume-dock-icon-v3.png'
$outputPath = Join-Path $PSScriptRoot 'Vellume.App\Assets\Brand\vellume-app.ico'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()
$source = [System.Drawing.Bitmap]::new($sourcePath)
try {
    if ($source.Width -ne $source.Height) {
        throw 'The app icon source must be square.'
    }
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new(
            $size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = $null
        $attributes = $null
        $png = $null
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
            $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $graphics.DrawImage($source,
                [System.Drawing.Rectangle]::new(0, 0, $size, $size),
                0, 0, $source.Width, $source.Height,
                [System.Drawing.GraphicsUnit]::Pixel, $attributes)
            $png = [System.IO.MemoryStream]::new()
            $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($png.ToArray())
        }
        finally {
            if ($png) { $png.Dispose() }
            if ($attributes) { $attributes.Dispose() }
            if ($graphics) { $graphics.Dispose() }
            $bitmap.Dispose()
        }
    }
}
finally {
    $source.Dispose()
}

# Windows 10/11 ICO directory with lossless 32-bit RGBA PNG frames.
# Write only after every frame has been produced successfully.
$ico = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($ico)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) {
        $writer.Write($frame)
    }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($outputPath, $ico.ToArray())
}
finally {
    $writer.Dispose()
    $ico.Dispose()
}
Write-Output "App icon: $outputPath ($($sizes -join ', ') px)"
