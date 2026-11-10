param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version,
    [string]$NuGetConfig = (Join-Path $PSScriptRoot '..\NuGet.Config')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (![OperatingSystem]::IsWindows() -or ![Environment]::Is64BitOperatingSystem) {
    throw 'Packaging requires Windows x64 and the .NET 10 SDK.'
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$config = (Resolve-Path $NuGetConfig).Path
$output = Join-Path $repo ".artifacts\installer\$Version"
if (Test-Path $output) { throw "Output already exists: $output. Choose a new version or explicitly remove this local build before rebuilding." }
$scratch = Join-Path $output 'build-staging'
$previousEnvironment = @{}
Push-Location $repo
try {
    $app = Join-Path $repo 'src\OrgExplorer.Desktop\OrgExplorer.Desktop.csproj'
    [xml]$project = Get-Content $app -Raw
    $tool = Get-Content '.config\dotnet-tools.json' -Raw | ConvertFrom-Json
    $client = @($project.Project.ItemGroup.PackageReference | Where-Object Include -EQ 'Velopack')[0]
    if ($client.Version -ne $tool.tools.vpk.version -or $tool.tools.vpk.version -ne '1.2.161') {
        throw 'Pin the Velopack client and local vpk packaging tool to the same reviewed version.'
    }
    New-Item -ItemType Directory $scratch -Force | Out-Null
    foreach ($name in 'TEMP', 'TMP', 'VELOPACK_TEMP') {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $scratch)
    }
    dotnet tool restore --configfile $config
    if ($LASTEXITCODE -ne 0) { throw 'Restore the pinned local vpk tool; no global installation is required.' }
    dotnet restore $app --locked-mode --configfile $config
    if ($LASTEXITCODE -ne 0) { throw 'Locked Windows x64 dependency/runtime restore failed.' }
    $publish = Join-Path $output 'publish'
    $releases = Join-Path $output 'releases'
    dotnet publish $app -c Release --runtime win-x64 --self-contained true --no-restore --output $publish `
        "-p:Version=$Version" "-p:InformationalVersion=$Version" -p:IncludeSourceRevisionInInformationalVersion=false `
        -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=false -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained .NET 10 WPF publish failed.' }
    dotnet tool run vpk -- pack --packId geevensingh.OrgExplorer --packTitle 'Org explorer' `
        --packAuthors 'Geeven Singh' --packVersion $Version --mainExe OrgExplorer.Desktop.exe `
        --runtime win11-x64 --channel win --packDir $publish --outputDir $releases `
        --delta None --skip-updates --yes
    if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed. Bootstrap validation must remain enabled.' }
    [ordered]@{
        schemaVersion = 1
        appId = 'geevensingh.OrgExplorer'
        version = $Version
        platform = 'win-x64'
        channel = 'win'
        minimumWindowsBuild = 22000
        selfContained = $true
        velopackVersion = '1.2.161'
        updateAuthorization = 'not-provided'
        artifacts = @(Get-ChildItem $releases -File | Sort-Object Name | ForEach-Object {
            [ordered]@{ fileName = $_.Name; size = $_.Length; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $releases 'package-metadata.json') -Encoding utf8
    & (Join-Path $repo 'tests\Installer.Artifacts.Tests.ps1') -ReleaseDirectory $releases -Version $Version
    Write-Host "Local unsigned installation artifacts: $releases"
    Write-Host 'Not published. Feed hashes/package-metadata are NOT signed update authorization; no unattended apply is enabled.'
} finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
    }
    if (Test-Path $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    Pop-Location
}
