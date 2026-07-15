param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$AssetsDirectory = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$AssetsDirectory = if ([string]::IsNullOrWhiteSpace($AssetsDirectory)) {
    Join-Path $PSScriptRoot '..\Assets'
} else {
    $AssetsDirectory
}
$assets = [IO.Path]::GetFullPath($AssetsDirectory)
[IO.Directory]::CreateDirectory($assets) | Out-Null
$sourceBitmap = [Drawing.Bitmap]::new([IO.Path]::GetFullPath($Source))
$clean = [Drawing.Bitmap]::new($sourceBitmap.Width, $sourceBitmap.Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)

try {
    for ($y = 0; $y -lt $sourceBitmap.Height; $y++) {
        for ($x = 0; $x -lt $sourceBitmap.Width; $x++) {
            $pixel = $sourceBitmap.GetPixel($x, $y)
            $maximum = [Math]::Max($pixel.R, [Math]::Max($pixel.G, $pixel.B))
            $minimum = [Math]::Min($pixel.R, [Math]::Min($pixel.G, $pixel.B))
            if ($minimum -ge 180 -and ($maximum - $minimum) -le 18) {
                $clean.SetPixel($x, $y, [Drawing.Color]::Transparent)
            }
            else {
                $clean.SetPixel($x, $y, [Drawing.Color]::FromArgb(255, $pixel.R, $pixel.G, $pixel.B))
            }
        }
    }

    function New-IconBitmap([int]$width, [int]$height, [double]$fill = 0.9) {
        $target = [Drawing.Bitmap]::new($width, $height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($target)
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceOver
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
            $side = [int][Math]::Floor([Math]::Min($width, $height) * $fill)
            $left = [int](($width - $side) / 2)
            $top = [int](($height - $side) / 2)
            $graphics.DrawImage($clean, $left, $top, $side, $side)
        }
        finally {
            $graphics.Dispose()
        }
        return $target
    }

    function Save-Png([string]$name, [int]$width, [int]$height, [double]$fill = 0.9) {
        $image = New-IconBitmap $width $height $fill
        try {
            $image.Save((Join-Path $assets $name), [Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $image.Dispose()
        }
    }

    Save-Png 'AppIconSource.png' 1024 1024 0.98
    Save-Png 'LockScreenLogo.scale-200.png' 48 48 0.9
    Save-Png 'Square150x150Logo.scale-200.png' 300 300 0.9
    Save-Png 'Square44x44Logo.scale-200.png' 88 88 0.9
    Save-Png 'Square44x44Logo.targetsize-24_altform-unplated.png' 24 24 0.92
    Save-Png 'Square44x44Logo.targetsize-48_altform-lightunplated.png' 48 48 0.92
    Save-Png 'StoreLogo.png' 50 50 0.9
    Save-Png 'SplashScreen.scale-200.png' 1240 600 0.42
    Save-Png 'Wide310x150Logo.scale-200.png' 620 300 0.42

    $iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $payloads = [Collections.Generic.List[byte[]]]::new()
    foreach ($size in $iconSizes) {
        $image = New-IconBitmap $size $size 0.92
        $stream = [IO.MemoryStream]::new()
        try {
            $image.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $payloads.Add($stream.ToArray())
        }
        finally {
            $stream.Dispose()
            $image.Dispose()
        }
    }

    $iconPath = Join-Path $assets 'AppIcon.ico'
    $file = [IO.File]::Create($iconPath)
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$iconSizes.Count)
        $offset = 6 + 16 * $iconSizes.Count
        for ($index = 0; $index -lt $iconSizes.Count; $index++) {
            $size = $iconSizes[$index]
            $writer.Write([byte]($(if ($size -eq 256) { 0 } else { $size })))
            $writer.Write([byte]($(if ($size -eq 256) { 0 } else { $size })))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$payloads[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $payloads[$index].Length
        }
        foreach ($payload in $payloads) {
            $writer.Write($payload)
        }
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }
}
finally {
    $clean.Dispose()
    $sourceBitmap.Dispose()
}

Write-Output "Generated Windows icon assets in $assets"
