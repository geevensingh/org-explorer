param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\..\eng\ReleaseArtifacts.ps1"
$release = (Resolve-Path $ReleaseDirectory).Path
$metadata = Get-Content (Join-Path $release 'package-metadata.json') -Raw | ConvertFrom-Json
if ($metadata.appId -ne 'geevensingh.OrgExplorer' -or $metadata.version -ne $Version -or
    $metadata.platform -ne 'win-x64' -or $metadata.channel -ne 'win' -or
    $metadata.minimumWindowsBuild -ne 22000 -or $metadata.selfContained -ne $true -or
    $metadata.velopackVersion -ne '1.2.161' -or $metadata.updateAuthorization -ne 'not-provided') {
    throw 'Package identity, runtime, channel, or authorization metadata does not match the installation contract.'
}
$setup = Join-Path $release 'geevensingh.OrgExplorer-win-Setup.exe'
$package = Join-Path $release "geevensingh.OrgExplorer-$Version-full.nupkg"
$portable = Join-Path $release 'geevensingh.OrgExplorer-win-Portable.zip'
Assert-ReleasePayload $package
Assert-ReleasePayload $portable
foreach ($file in $setup, $package, $portable, (Join-Path $release 'releases.win.json'),
    (Join-Path $release 'assets.win.json'), (Join-Path $release 'RELEASES')) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required Velopack artifact is missing: $file" }
}
foreach ($item in $metadata.artifacts) {
    if ($item.fileName -ne [IO.Path]::GetFileName($item.fileName) -or $item.fileName -match '[/\\]') {
        throw 'Artifact inventory must contain only relative filenames.'
    }
    $file = Join-Path $release $item.fileName
    if ($item.size -ne (Get-Item $file).Length -or $item.sha256 -ne (Get-FileHash $file -Algorithm SHA256).Hash) {
        throw "Artifact inventory hash/size does not match: $($item.fileName)"
    }
}
$feed = Get-Content (Join-Path $release 'releases.win.json') -Raw | ConvertFrom-Json
$asset = @($feed.Assets | Where-Object { $_.Type -eq 'Full' -and $_.Version -eq $Version })
if ($asset.Count -ne 1 -or $asset[0].FileName -ne [IO.Path]::GetFileName($package) -or
    $asset[0].Size -ne (Get-Item $package).Length -or
    $asset[0].SHA256 -ne (Get-FileHash $package -Algorithm SHA256).Hash) {
    throw 'Velopack feed does not bind the expected full package, version, size, and SHA256.'
}
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $manifestEntry = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
    if ($manifestEntry.Count -ne 1) { throw 'Full package has no unambiguous package manifest.' }
    $reader = [IO.StreamReader]::new($manifestEntry[0].Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $ns.AddNamespace('n', $manifest.DocumentElement.NamespaceURI)
    $get = {
        param($name)
        $node = $manifest.SelectSingleNode("//n:metadata/n:$name", $ns)
        if ($null -eq $node) { return '' }
        $node.InnerText
    }
    if ((& $get 'id') -ne 'geevensingh.OrgExplorer' -or (& $get 'version') -ne $Version -or
        (& $get 'channel') -ne 'win' -or (& $get 'machineArchitecture') -ne 'x64' -or
        (& $get 'osMinVersion') -ne '11' -or (& $get 'mainExe') -ne 'OrgExplorer.Desktop.exe') {
        throw 'Installed Velopack manifest identity/platform/channel does not match the build.'
    }
    if ((& $get 'runtimeDependencies') -ne '') {
        throw 'Self-contained installation unexpectedly requires a separately downloaded runtime.'
    }
    $appEntries = @($archive.Entries | Where-Object { $_.FullName -Match '^lib/[^/]+/.+' -and $_.Name -ne '' })
    foreach ($name in 'OrgExplorer.Desktop.exe', 'OrgExplorer.Desktop.dll', 'OrgExplorer.Workflow.dll',
        'Velopack.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
        'PresentationFramework.dll', 'PresentationCore.dll', 'wpfgfx_cor3.dll', 'INSTALLATION.md') {
        if (!($appEntries | Where-Object Name -EQ $name)) { throw "Missing real app, help, or bundled desktop runtime: $name" }
    }
    foreach ($entry in $appEntries) {
        $extension = [IO.Path]::GetExtension($entry.Name)
        if ($extension -notin '.dll', '.exe', '.json', '.config', '.xml', '.md', '.txt' -and $entry.Name -ne 'sq.version') {
            throw "Unexpected non-product file in application package: $($entry.FullName)"
        }
        if ($extension -eq '.json' -and $entry.Name -notin 'OrgExplorer.Desktop.runtimeconfig.json', 'OrgExplorer.Desktop.deps.json') {
            throw "Unexpected JSON/Workspace or configuration data in application package: $($entry.Name)"
        }
    }
    if ($archive.Entries.FullName -match '(^|/)(people|photos|notes|history|generations|\.git|\.test-workspaces)/|workspace\.json|\.pfx$|\.pem$|\.key$|\.pdb$') {
        throw 'Private data, credentials, source/debug data, or a developer Workspace is bundled.'
    }
    $runtime = $appEntries | Where-Object Name -EQ 'OrgExplorer.Desktop.runtimeconfig.json'
    $reader = [IO.StreamReader]::new($runtime.Open())
    try { $config = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($config.runtimeOptions.PSObject.Properties.Name -contains 'framework' -or
        $config.runtimeOptions.PSObject.Properties.Name -contains 'frameworks' -or
        @($config.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' -and $_.version -like '10.0.*' }).Count -ne 1) {
        throw 'Application is not a self-contained .NET 10 WindowsDesktop deployment.'
    }
    foreach ($name in 'OrgExplorer.Desktop.exe', 'coreclr.dll', 'wpfgfx_cor3.dll') {
        $entry = $appEntries | Where-Object Name -EQ $name
        $stream = [IO.MemoryStream]::new()
        try {
            $input = $entry.Open()
            try { $input.CopyTo($stream) } finally { $input.Dispose() }
            $reader = [IO.BinaryReader]::new($stream)
            $stream.Position = 0x3c
            $peOffset = $reader.ReadInt32()
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
                throw "Packaged native binary does not target Windows x64: $name"
            }
        } finally { $stream.Dispose() }
    }
} finally { $archive.Dispose() }
$archive = [IO.Compression.ZipFile]::OpenRead($portable)
try {
    foreach ($name in '.portable', 'Org explorer.exe', 'Update.exe', 'current/OrgExplorer.Desktop.exe', 'current/sq.version') {
        if (!($archive.Entries | Where-Object FullName -EQ $name)) { throw "Required portable launcher/bootstrap artifact is missing: $name" }
    }
} finally { $archive.Dispose() }
Write-Host 'PASS: installer, full update package, feed, portable bundle, coherent metadata, bundled .NET 10/WPF runtime, and product-only payload.'
