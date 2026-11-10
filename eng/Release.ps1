$ReleaseRepository = 'geevensingh/org-explorer'
$ReleaseManifestName = 'org-explorer-update.win-x64.json'
. "$PSScriptRoot\ReleaseSigning.ps1"
. "$PSScriptRoot\ReleaseGates.ps1"
. "$PSScriptRoot\ReleaseGitHub.ps1"
. "$PSScriptRoot\ReleaseArtifacts.ps1"

function Assert-ReleaseVersion([string]$Version) {
    if ($Version -cnotmatch '^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z') {
        throw 'Release versions must be canonical stable major.minor.patch, each component at most 999999999.'
    }
}

function Get-NextReleaseVersion([string]$InitialVersion, [string]$LastVersion) {
    Assert-ReleaseVersion $InitialVersion
    if (!$LastVersion) { return $InitialVersion }
    Assert-ReleaseVersion $LastVersion
    $parts = $LastVersion.Split('.')
    if ([int]$parts[2] -eq 999999999) { throw 'Patch version space exhausted; an owner-reviewed version policy change is required.' }
    return "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
}

function Test-ProductImpact([string[]]$Paths, [string[]]$ShippedResources) {
    foreach ($path in $Paths) {
        if ($path -cin $ShippedResources -or
            $path -cmatch '^(src/|assets/|schemas/|Directory\.(Build|Packages)\.|NuGet\.Config$|global\.json$|\.config/dotnet-tools\.json$|eng/(Package-Windows|Release[^/]*|Invoke-Release)\.ps1$|\.github/workflows/release\.yml$)') { return $true }
        if ($path -cmatch '^(docs/|tests/|eng/|\.github/)' -or
            $path -cmatch '(^|/)(README|AGENTS|CLAUDE|GLOSSARY|LICENSE)(\.[^/]*)?$' -or
            $path -cin @('.gitignore', '.gitattributes', '.editorconfig')) { continue }
        # Unknown build inputs are conservative product changes, not silent exclusions.
        return $true
    }

    return $false
}

function Invoke-ReleaseGit([string]$Root, [string[]]$Arguments) {
    $result = & git -C $Root @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Cannot establish release source history. Fetch full history and tags; do not guess a comparison baseline.' }
    return $result
}

function Get-ShippedResources([string]$Root, [string]$Revision) {
    $files = @(Invoke-ReleaseGit $Root @('ls-tree', '-r', '--name-only', $Revision))
    foreach ($projectPath in @($files | Where-Object { $_ -cmatch '^src/.+\.csproj$' })) {
        [xml]$project = (Invoke-ReleaseGit $Root @('show', "${Revision}:$projectPath")) -join "`n"
        $projectDirectory = [IO.Path]::GetDirectoryName((Join-Path $Root $projectPath))
        $properties = @{ MSBuildProjectDirectory = $projectDirectory; MSBuildThisFileDirectory = $projectDirectory + '\' }
        foreach ($property in $project.SelectNodes('//PropertyGroup/*')) { $properties[$property.LocalName] = $property.InnerText }
        foreach ($item in $project.SelectNodes('//Content[@Include or @Update] | //EmbeddedResource[@Include or @Update] | //None[(@CopyToPublishDirectory or @CopyToOutputDirectory) and (@Include or @Update)]')) {
            if ($item.LocalName -ne 'EmbeddedResource' -and
                $item.GetAttribute('CopyToPublishDirectory') -ceq 'Never' -and
                $item.GetAttribute('CopyToOutputDirectory') -cin @('', 'Never')) { continue }
            $include = if ($item.HasAttribute('Include')) { $item.GetAttribute('Include') } else { $item.GetAttribute('Update') }
            for ($pass = 0; $pass -lt 16 -and $include.Contains('$('); $pass++) {
                $previous = $include
                $include = [regex]::Replace($include, '\$\(([A-Za-z_][A-Za-z0-9_]*)\)', {
                    param($match)
                    if ($properties.ContainsKey($match.Groups[1].Value)) { return $properties[$match.Groups[1].Value] }
                    return $match.Value
                })
                if ($include -ceq $previous) { break }
            }
            if ($include.Contains('$(') -or $include.Contains('@(') -or $include.Contains('%(')) {
                throw 'Cannot classify a shipped MSBuild resource expression. Add explicit repository-relative resource inputs; never silently exclude shipped documentation.'
            }
            foreach ($part in $include.Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
                $absolute = [IO.Path]::GetFullPath($part, $projectDirectory)
                $relative = [IO.Path]::GetRelativePath($Root, $absolute).Replace('\', '/')
                if ($relative.StartsWith('../')) { throw 'Shipped resources outside the checkout cannot be classified reproducibly.' }
                foreach ($file in $files) {
                    if ($file -clike $relative) { $file }
                }
            }
        }
    }
}

function Get-ReleasePlan([string]$Root, [string]$Candidate, $LastRelease = $null, [string[]]$ReservedVersions = @()) {
    if ($Candidate -cnotmatch '^[0-9a-f]{40}$') { throw 'A release candidate must be an exact full source SHA.' }
    [xml]$project = (Invoke-ReleaseGit $Root @('show', "${Candidate}:src/OrgExplorer.Desktop/OrgExplorer.Desktop.csproj")) -join "`n"
    $initial = [string]$project.Project.PropertyGroup.Version
    $lastSource = ''
    $lastTag = ''
    $lastVersion = ''
    $resources = @(Get-ShippedResources $Root $Candidate)
    if ($null -ne $LastRelease) {
        $lastSource = [string]$LastRelease.sourceSha
        $lastTag = [string]$LastRelease.tag
        $lastVersion = [string]$LastRelease.version
        if ($lastSource -cnotmatch '^[0-9a-f]{40}$' -or $lastTag -cne "v$lastVersion") { throw 'Last publication has invalid source/version provenance.' }
        $null = Invoke-ReleaseGit $Root @('merge-base', '--is-ancestor', $lastSource, $Candidate)
        $paths = @(Invoke-ReleaseGit $Root @('diff', '--name-only', '--no-renames', $lastSource, $Candidate))
        $resources += @(Get-ShippedResources $Root $lastSource)
    } else {
        $paths = @(Invoke-ReleaseGit $Root @('ls-tree', '-r', '--name-only', $Candidate))
    }
    $publish = Test-ProductImpact $paths $resources
    $version = Get-NextReleaseVersion -InitialVersion $initial -LastVersion $lastVersion
    while ($version -cin $ReservedVersions) {
        $version = Get-NextReleaseVersion -InitialVersion $initial -LastVersion $version
    }
    return [ordered]@{
        schemaVersion = 1; publish = $publish; sourceSha = $Candidate; lastSourceSha = $lastSource; lastTag = $lastTag
        version = $version; tag = "v$version"; changedPaths = $paths
        reason = if ($publish) { 'Unpublished product effect since the last successful publication.' } else { 'No unpublished product effect.' }
    }
}
