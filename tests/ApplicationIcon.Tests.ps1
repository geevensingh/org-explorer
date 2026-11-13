param(
    [string]$Icon = "$PSScriptRoot\..\src\OrgExplorer.Desktop\Assets\OrgExplorer.ico",
    [string]$Executable
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName PresentationCore, WindowsBase

$bytes = [IO.File]::ReadAllBytes($Icon)
$stream = [IO.MemoryStream]::new($bytes, $false)
$reader = [IO.BinaryReader]::new($stream)
try {
    if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1) { throw 'Expected a native Windows icon, not renamed artwork.' }
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    if ($reader.ReadUInt16() -ne $sizes.Count) { throw 'Missing native small, scaled taskbar, large, or shell icon frames.' }
    $nextOffset = 6 + 16 * $sizes.Count
    foreach ($size in $sizes) {
        $width = $reader.ReadByte()
        $height = $reader.ReadByte()
        if ($width -eq 0) { $width = 256 }
        if ($height -eq 0) { $height = 256 }
        if ($width -ne $size -or $height -ne $size -or $reader.ReadByte() -ne 0 -or
            $reader.ReadByte() -ne 0 -or $reader.ReadUInt16() -ne 1 -or $reader.ReadUInt16() -ne 32) {
            throw "Invalid native frame directory at size $size."
        }
        $length = $reader.ReadUInt32()
        $offset = $reader.ReadUInt32()
        if ($offset -ne $nextOffset -or $length -le 0 -or $offset + $length -gt $bytes.Length) {
            throw "Truncated, overlapping, or extraneous frame data at size $size."
        }
        $nextOffset += $length
        $frameStream = [IO.MemoryStream]::new($bytes, [int]$offset, [int]$length, $false)
        try {
            $decoder = [Windows.Media.Imaging.PngBitmapDecoder]::new($frameStream,
                [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
            $frame = $decoder.Frames[0]
            if ($decoder.Frames.Count -ne 1 -or $frame.PixelWidth -ne $size -or $frame.PixelHeight -ne $size) {
                throw "Frame payload does not decode to its advertised size $size."
            }
            $image = [Windows.Media.Imaging.FormatConvertedBitmap]::new($frame, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
            $pixels = [byte[]]::new($size * $size * 4)
            $image.CopyPixels($pixels, $size * 4, 0)
            # Independent probes catch blank, generic, flattened, or clipped exports, not visual acceptance.
            foreach ($probe in @(
                @{ X = 0.00; Y = 0.00; Kind = 'transparent' },
                @{ X = 0.15; Y = 0.50; Kind = 'blue' },
                @{ X = 0.52; Y = 0.30; Kind = 'white' },
                @{ X = 0.53; Y = 0.45; Kind = 'blue' },
                @{ X = 0.53; Y = 0.65; Kind = 'blue' },
                @{ X = 0.53; Y = 0.75; Kind = 'white' }
            )) {
                $index = 4 * ([int][Math]::Floor($size * $probe.Y) * $size + [int][Math]::Floor($size * $probe.X))
                $b, $g, $r, $a = $pixels[$index..($index + 3)]
                $valid = switch ($probe.Kind) {
                    transparent { $a -eq 0 }
                    blue { $a -ge 245 -and $b -gt 140 -and $b -gt $r + 60 -and $g -gt $r + 20 }
                    white { $a -ge 245 -and $r -ge 230 -and $g -ge 230 -and $b -ge 230 }
                }
                if (!$valid) { throw "The $size-pixel C1 export lost its $($probe.Kind) tile/card/Person content at $($probe.X),$($probe.Y)." }
            }
        } finally { $frameStream.Dispose() }
    }
    if ($nextOffset -ne $bytes.Length) { throw 'Unexpected content outside the icon frames.' }
} finally { $reader.Dispose() }
if ($Executable) {
    if (!('OrgExplorer.TestFixtures.NativeIconResource' -as [type])) { Add-Type -Path "$PSScriptRoot\NativeIconResource.cs" }
    $embedded = [OrgExplorer.TestFixtures.NativeIconResource]::ReadExecutableIcon($Executable)
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($embedded)) -ne
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))) {
        throw "The default executable icon does not contain the exact tested C1 frames: $Executable"
    }
}
Write-Host 'PASS: decoded 16/20/24/32/40/48/64/96/128/256 native C1 frames, transparency, card, and Person content.'
