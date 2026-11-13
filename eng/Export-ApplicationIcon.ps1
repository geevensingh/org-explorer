param(
    [string]$Output = "$PSScriptRoot\..\src\OrgExplorer.Desktop\Assets\OrgExplorer.ico",
    [string]$PreviewDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$artwork = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText("$PSScriptRoot\..\assets\app-icon.xaml"))
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = [Collections.Generic.List[byte[]]]::new()
if ($PreviewDirectory) { [void][IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($PreviewDirectory)) }
foreach ($size in $sizes) {
    $small = $size -le 24
    $drawing = $artwork[$(if ($small) { 'Small' } else { 'Large' })].Drawing
    $scale = $size / $(if ($small) { 16.0 } else { 256.0 })
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.PushTransform([Windows.Media.ScaleTransform]::new($scale, $scale))
    $context.DrawDrawing($drawing)
    $context.Pop()
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); $frames.Add($stream.ToArray()) } finally { $stream.Dispose() }
    if ($PreviewDirectory) { [IO.File]::WriteAllBytes((Join-Path $PreviewDirectory "$size.png"), $frames[$frames.Count - 1]) }
}
$path = [IO.Path]::GetFullPath($Output)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
$writer = [IO.BinaryWriter]::new([IO.File]::Create($path))
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose() }
Write-Host "Exported original C1 artwork to $path"
