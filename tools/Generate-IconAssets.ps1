[CmdletBinding()]
param(
    [string]$SourcePath
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $repositoryRoot 'BetterBluetoothAudioConnector\Assets'

if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path $assetDirectory 'BetterBluetoothAudioConnector-Source.png'
}

$SourcePath = (Resolve-Path -LiteralPath $SourcePath).Path

Add-Type -AssemblyName System.Drawing

function New-ResizedPngBytes {
    param(
        [Parameter(Mandatory)]
        [System.Drawing.Bitmap]$Source,

        [Parameter(Mandatory)]
        [int]$CanvasWidth,

        [Parameter(Mandatory)]
        [int]$CanvasHeight,

        [Parameter(Mandatory)]
        [int]$ImageWidth,

        [Parameter(Mandatory)]
        [int]$ImageHeight
    )

    $bitmap = [System.Drawing.Bitmap]::new(
        $CanvasWidth,
        $CanvasHeight,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

            $x = [int](($CanvasWidth - $ImageWidth) / 2)
            $y = [int](($CanvasHeight - $ImageHeight) / 2)
            $destination = [System.Drawing.Rectangle]::new($x, $y, $ImageWidth, $ImageHeight)
            $graphics.DrawImage(
                $Source,
                $destination,
                0,
                0,
                $Source.Width,
                $Source.Height,
                [System.Drawing.GraphicsUnit]::Pixel)
        }
        finally {
            $graphics.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

function Write-PngAsset {
    param(
        [Parameter(Mandatory)]
        [System.Drawing.Bitmap]$Source,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [int]$CanvasWidth,

        [Parameter(Mandatory)]
        [int]$CanvasHeight,

        [Parameter(Mandatory)]
        [int]$ImageWidth,

        [Parameter(Mandatory)]
        [int]$ImageHeight
    )

    [byte[]]$bytes = New-ResizedPngBytes `
        -Source $Source `
        -CanvasWidth $CanvasWidth `
        -CanvasHeight $CanvasHeight `
        -ImageWidth $ImageWidth `
        -ImageHeight $ImageHeight

    $path = Join-Path $assetDirectory $Name
    [System.IO.File]::WriteAllBytes($path, $bytes)
}

$source = [System.Drawing.Bitmap]::new($SourcePath)

try {
    $assets = @(
        @{ Name = 'LargeTile.scale-200.png'; CanvasWidth = 620; CanvasHeight = 620; ImageWidth = 620; ImageHeight = 620 },
        @{ Name = 'SmallTile.scale-200.png'; CanvasWidth = 142; CanvasHeight = 142; ImageWidth = 142; ImageHeight = 142 },
        @{ Name = 'SplashScreen.scale-200.png'; CanvasWidth = 1240; CanvasHeight = 600; ImageWidth = 500; ImageHeight = 500 },
        @{ Name = 'Square150x150Logo.scale-200.png'; CanvasWidth = 300; CanvasHeight = 300; ImageWidth = 300; ImageHeight = 300 },
        @{ Name = 'Square44x44Logo.scale-200.png'; CanvasWidth = 88; CanvasHeight = 88; ImageWidth = 88; ImageHeight = 88 },
        @{ Name = 'StoreLogo.scale-200.png'; CanvasWidth = 100; CanvasHeight = 100; ImageWidth = 100; ImageHeight = 100 },
        @{ Name = 'Wide310x150Logo.scale-200.png'; CanvasWidth = 620; CanvasHeight = 300; ImageWidth = 256; ImageHeight = 256 }
    )

    foreach ($asset in $assets) {
        Write-PngAsset -Source $source @asset
    }

    $targetSizes = @(16, 24, 32, 48, 256)
    foreach ($size in $targetSizes) {
        $names = @(
            "Square44x44Logo.targetsize-$size.png",
            "Square44x44Logo.altform-lightunplated_targetsize-$size.png"
        )

        if ($size -eq 24) {
            $names += 'Square44x44Logo.targetsize-24_altform-unplated.png'
        }
        else {
            $names += "Square44x44Logo.altform-unplated_targetsize-$size.png"
        }

        foreach ($name in $names) {
            Write-PngAsset `
                -Source $source `
                -Name $name `
                -CanvasWidth $size `
                -CanvasHeight $size `
                -ImageWidth $size `
                -ImageHeight $size
        }
    }

    $iconSizes = @(16, 24, 32, 48, 64, 128, 256)
    $iconImages = foreach ($size in $iconSizes) {
        [byte[]]$data = New-ResizedPngBytes `
            -Source $source `
            -CanvasWidth $size `
            -CanvasHeight $size `
            -ImageWidth $size `
            -ImageHeight $size

        [pscustomobject]@{
            Size = $size
            Data = $data
        }
    }

    $iconPath = Join-Path $assetDirectory 'BetterBluetoothAudioConnector.ico'
    $iconStream = [System.IO.File]::Open(
        $iconPath,
        [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)

    try {
        $writer = [System.IO.BinaryWriter]::new($iconStream)
        try {
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]$iconImages.Count)

            [uint32]$offset = 6 + (16 * $iconImages.Count)
            foreach ($image in $iconImages) {
                $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
                $writer.Write([byte]$dimension)
                $writer.Write([byte]$dimension)
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]32)
                $writer.Write([uint32]$image.Data.Length)
                $writer.Write($offset)
                $offset += [uint32]$image.Data.Length
            }

            foreach ($image in $iconImages) {
                $writer.Write([byte[]]$image.Data)
            }
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $iconStream.Dispose()
    }
}
finally {
    $source.Dispose()
}

Get-ChildItem -LiteralPath $assetDirectory -File |
    Where-Object { $_.Extension -in '.png', '.ico' } |
    Sort-Object Name |
    Select-Object Name, Length
