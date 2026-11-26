param([string] $RepositoryPath = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($RepositoryPath))
$entry = Join-Path $root 'docs\testing.md'

function Get-LocalPath([string] $Source, [string] $Destination) {
    $path = [uri]::UnescapeDataString(($Destination -split '[?#]', 2)[0])
    if (!$path) { return $Source }
    if ($path.StartsWith('/')) { $path = Join-Path $root $path.TrimStart('/') }
    else { $path = Join-Path (Split-Path $Source -Parent) $path }
    return [IO.Path]::GetFullPath($path)
}

function Assert-RepositoryPath([string] $Path, [string] $Diagnostic) {
    if (!$Path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "${Diagnostic}: destination leaves repository"
    }
    $ancestor = $root
    foreach ($part in [IO.Path]::GetRelativePath($root, $Path).Split([IO.Path]::DirectorySeparatorChar)) {
        $ancestor = Join-Path $ancestor $part
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "${Diagnostic}: destination is a reparse point"
            }
        }
    }
}

Assert-RepositoryPath $entry 'docs\testing.md'
$lineCount = [IO.File]::ReadAllLines($entry).Length
if ($lineCount -gt 300) { throw "docs\testing.md: $lineCount physical lines exceeds 300" }

function Read-Document([string] $Path) {
    $links = [Collections.Generic.List[object]]::new()
    $anchors = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $text = [regex]::Replace([IO.File]::ReadAllText($Path), '(?s)<!--.*?-->', {
        param($match)
        [regex]::Replace($match.Value, '[^\r\n]', ' ')
    })
    $lines = $text -split '\r?\n'
    $sections = @{}
    $section = ''
    $fence = ''
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($fence) {
            if ($lines[$i] -match ('^ {0,3}' + [regex]::Escape($fence[0]) + "{$($fence.Length),}\s*$")) { $fence = '' }
            $lines[$i] = ''
            continue
        }
        if ($lines[$i] -match '^ {0,3}(`{3,}|~{3,})') {
            $fence = $Matches[1]
            $lines[$i] = ''
            continue
        }
        if ($lines[$i] -match '^(?: {4}|\t)') { $lines[$i] = ''; continue }
        if ($lines[$i] -match '^ {0,3}#{1,6}(?:[ \t]+|$)(.*)$') {
            $heading = [regex]::Replace($Matches[1], '\s+#+\s*$', '').Trim()
            $baseSlug = [regex]::Replace($heading.ToLowerInvariant(), '[^\p{L}\p{N}\p{M}_ -]', '').Replace(' ', '-')
            $slug = $baseSlug
            $suffix = 0
            while ($anchors.Contains($slug)) { $slug = "$baseSlug-$((++$suffix))" }
            $null = $anchors.Add($slug)
            $section = $slug
        }
        $sections[$i] = $section
    }
    $navigation = [regex]::Replace(($lines -join "`n"), '(?s)(?<!`)(`+)(?!`).*?(?<!`)\1(?!`)', {
        param($match)
        [regex]::Replace($match.Value, '[^\r\n]', ' ')
    }) -split '\n'
    for ($i = 0; $i -lt $navigation.Length; $i++) {
        foreach ($match in [regex]::Matches($navigation[$i], '\[[^\]]+\]\((?<destination>[^)\s]+)\)')) {
            $links.Add(@{ line = $i + 1; destination = $match.Groups['destination'].Value; section = $sections[$i] })
        }
    }
    return @{ links = $links; lines = $lines; anchors = $anchors }
}

$referenceDirectory = Join-Path $root 'docs\testing'
Assert-RepositoryPath $referenceDirectory 'docs\testing'
$references = @(Get-ChildItem -LiteralPath $referenceDirectory -Filter *.md -File |
    ForEach-Object { Get-LocalPath $entry ('testing/' + $_.Name) })
$documents = @{}
foreach ($path in @($entry) + $references) {
    Assert-RepositoryPath $path ([IO.Path]::GetRelativePath($root, $path))
    $documents[$path] = Read-Document $path
}
$inbound = @(git -c core.quotepath=false -C $root grep -l -I -e 'testing.md' -e 'testing/' -- '*.md')
if ($LASTEXITCODE -notin @(0, 1)) { throw 'Cannot discover tracked inbound Testing links.' }
$inboundFiles = 0
foreach ($relative in $inbound) {
    $path = Get-LocalPath $entry ('/' + $relative)
    if (!$documents.ContainsKey($path)) {
        Assert-RepositoryPath $path $relative
        $documents[$path] = Read-Document $path
        $inboundFiles++
    }
}
$localLinks = 0
$inboundLinks = 0
$edges = @{}
$anchorDocuments = @{} + $documents
foreach ($path in $documents.Keys) {
    $owned = $path -eq $entry -or $path -in $references
    $edges[$path] = [Collections.Generic.List[string]]::new()
    foreach ($link in $documents[$path].links) {
        if ($link.destination -match '^(?:[a-zA-Z][a-zA-Z0-9+.-]*:|//)') { continue }
        $destination = Get-LocalPath $path $link.destination
        if (!$owned -and $destination -ne $entry -and $destination -notin $references) { continue }
        $source = [IO.Path]::GetRelativePath($root, $path)
        Assert-RepositoryPath $destination "${source}:$($link.line): $($link.destination)"
        if (![IO.File]::Exists($destination)) {
            throw "${source}:$($link.line): missing local file: $($link.destination)"
        }
        if ($link.destination.Contains('#')) {
            $fragment = [uri]::UnescapeDataString(($link.destination -split '#', 2)[1])
            if (!$anchorDocuments.ContainsKey($destination)) { $anchorDocuments[$destination] = Read-Document $destination }
            if (!$anchorDocuments[$destination].anchors.Contains($fragment)) {
                throw "${source}:$($link.line): unresolved fragment: $($link.destination)"
            }
        }
        $localLinks++
        if (!$owned) { $inboundLinks++ }
        $edges[$path].Add($destination)
    }
}
$reachable = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$pending = [Collections.Generic.Queue[string]]::new()
$pending.Enqueue($entry)
while ($pending.Count) {
    $path = $pending.Dequeue()
    if (!$reachable.Add($path)) { continue }
    if ($path -eq $entry -or $path -in $references) {
        foreach ($destination in $edges[$path]) { $pending.Enqueue($destination) }
    }
}
foreach ($path in $references) {
    $source = [IO.Path]::GetRelativePath($root, $path)
    if (!$reachable.Contains($path)) { throw "${source}: reference is unreachable from docs\testing.md" }
    if ($entry -notin $edges[$path]) { throw "${source}: missing return link to docs\testing.md" }
}
$forwarding = @{
    'retired-engine-coverage-reconciliation-100' = 'testing/workspace-storage-recovery.md#retired-engine-coverage-reconciliation-100'
    'required-prmain-execution' = 'testing/ci-and-artifacts.md#required-prmain-execution'
    'real-desktop-execution' = 'testing/desktop-harness.md#running-required-desktop-suites'
    'separate-scale-commands-and-results' = 'testing/scale-verification.md#separate-scale-commands-and-results'
    'nightly-and-manual-execution' = 'testing/release-and-manual.md#nightly-and-manual-execution'
    'active-pre-publication-integration' = 'testing/release-and-manual.md#active-pre-publication-integration'
}
foreach ($anchor in @('testing', 'placement', 'moving-coverage', 'isolation-and-remaining-blockers') + @($forwarding.Keys)) {
    if (!$documents[$entry].anchors.Contains($anchor)) { throw "docs\testing.md: missing original anchor #$anchor" }
}
foreach ($anchor in $forwarding.Keys) {
    $expected = Get-LocalPath $entry $forwarding[$anchor]
    $expectedFragment = ($forwarding[$anchor] -split '#', 2)[1]
    if (!@($documents[$entry].links | Where-Object {
        $_.section -ceq $anchor -and $_.destination.Contains('#') -and
            (Get-LocalPath $entry $_.destination) -eq $expected -and
            [uri]::UnescapeDataString(($_.destination -split '#', 2)[1]) -ceq $expectedFragment
    }).Count) {
        throw "docs\testing.md: compatibility section #$anchor must forward to $($forwarding[$anchor])"
    }
}
@{ entryLines = $lineCount; referenceFiles = $references.Count; localLinks = $localLinks
    inboundFiles = $inboundFiles; inboundLinks = $inboundLinks; compatibilityLinks = $forwarding.Count } |
    ConvertTo-Json -Compress
