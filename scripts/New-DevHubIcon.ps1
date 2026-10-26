# Rebuild the native Windows icon without external graphics dependencies.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/DevHub.App/Assets'))
$null = New-Item -ItemType Directory -Path $assetDirectory -Force

function New-RoundedRectangle([single]$x, [single]$y, [single]$width, [single]$height, [single]$radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

$frames = foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    # Render at 4x resolution so small taskbar and window icons retain clean edges.
    $canvas = [Drawing.Bitmap]::new($size * 4, $size * 4)
    $graphics = [Drawing.Graphics]::FromImage($canvas)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size * 4 / 256.0, $size * 4 / 256.0)
    $background = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#9ACCD3'))
    $foreground = [Drawing.Pen]::new([Drawing.Color]::White, 12)
    $foreground.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $foreground.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $foreground.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $tile = New-RoundedRectangle 8 8 240 240 54
    $terminal = New-RoundedRectangle 51 62 154 134 21
    $graphics.FillPath($background, $tile)
    $graphics.DrawPath($foreground, $terminal)
    $graphics.DrawLine($foreground, 54, 99, 202, 99)
    $graphics.DrawLine($foreground, 78, 125, 99, 145)
    $graphics.DrawLine($foreground, 99, 145, 78, 165)
    $graphics.DrawLine($foreground, 126, 166, 164, 166)
    $image = [Drawing.Bitmap]::new($size, $size)
    $scaled = [Drawing.Graphics]::FromImage($image)
    $scaled.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $scaled.DrawImage($canvas, 0, 0, $size, $size)
    $png = [IO.MemoryStream]::new()
    $image.Save($png, [Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $image.Save((Join-Path $assetDirectory 'DevHub.png'), [Drawing.Imaging.ImageFormat]::Png) }
    [pscustomobject]@{ Size = $size; Bytes = $png.ToArray() }
    $png.Dispose(); $scaled.Dispose(); $image.Dispose()
    $terminal.Dispose(); $tile.Dispose(); $foreground.Dispose(); $background.Dispose(); $graphics.Dispose(); $canvas.Dispose()
}

$iconStream = [IO.File]::Create((Join-Path $assetDirectory 'DevHub.ico'))
$writer = [IO.BinaryWriter]::new($iconStream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $iconStream.Dispose() }
Write-Output "Generated DevHub.ico with $($frames.Count) sizes and DevHub.png."
