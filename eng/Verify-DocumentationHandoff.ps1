param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$SourceBranch,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$DefaultBranch,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$BaseCommit,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$DocumentationCommit,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]]$MarkdownFiles,
    [ValidateNotNullOrEmpty()][string]$RepositoryPath = (Split-Path $PSScriptRoot -Parent),
    [ValidateNotNullOrEmpty()][string]$Remote = 'origin'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-HandoffGit([string[]]$Arguments) {
    $output = & git --no-replace-objects -C $RepositoryPath -c credential.helper= `
        -c 'credential.helper=!gh auth git-credential' @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Git $($Arguments[0]) failed: $($output -join "`n")" }
    return $output
}
function Test-HandoffAncestor([string]$Ancestor, [string]$Descendant) {
    $output = & git --no-replace-objects -C $RepositoryPath merge-base --is-ancestor $Ancestor $Descendant 2>&1
    if ($LASTEXITCODE -eq 0) { return $true }
    if ($LASTEXITCODE -eq 1) { return $false }
    throw "Cannot verify documentation ancestry: $($output -join "`n")"
}

foreach ($sha in @($BaseCommit, $DocumentationCommit)) {
    if ($sha -cnotmatch '^[0-9a-f]{40}$') { throw 'Base and documentation commits must be full lowercase commit SHAs.' }
}
$expectedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($path in $MarkdownFiles) {
    if ([string]::IsNullOrWhiteSpace($path) -or !$path.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The handoff file list must contain repository-relative Markdown paths.'
    }
    if (!$expectedPaths.Add($path)) { throw "Duplicate Markdown path: $path" }
}
if ((Invoke-HandoffGit @('rev-parse', '--is-shallow-repository')) -cne 'false') {
    throw 'Documentation verification requires a full-history source clone.'
}
if ($Remote -cnotin @(Invoke-HandoffGit @('remote'))) { throw 'The source remote must be a configured remote in this repository.' }
$null = Invoke-HandoffGit @('check-ref-format', "refs/heads/$SourceBranch")
$null = Invoke-HandoffGit @('check-ref-format', "refs/heads/$DefaultBranch")

$temporaryRef = 'refs/documentation-handoff/' + [guid]::NewGuid().ToString('N')
$sourceRef = "$temporaryRef/source"
$defaultRef = "$temporaryRef/default"
try {
    # An empty refmap prevents updates to configured remote-tracking refs.
    $null = Invoke-HandoffGit @('fetch', '--quiet', '--no-tags', '--no-write-fetch-head', '--no-recurse-submodules', '--refmap=', '--', $Remote,
        "+refs/heads/${SourceBranch}:$sourceRef", "+refs/heads/${DefaultBranch}:$defaultRef")
    $sourceTip = Invoke-HandoffGit @('rev-parse', '--verify', "$sourceRef^{commit}")
    $defaultTip = Invoke-HandoffGit @('rev-parse', '--verify', "$defaultRef^{commit}")
    foreach ($sha in @($BaseCommit, $DocumentationCommit)) {
        if ((Invoke-HandoffGit @('cat-file', '-t', $sha)) -cne 'commit') { throw 'The handoff SHAs must identify commit objects.' }
    }
    $parents = (Invoke-HandoffGit @('rev-list', '--parents', '-n', '1', $DocumentationCommit)).Split(' ')
    if ($parents.Count -ne 2 -or $parents[1] -cne $BaseCommit) {
        throw 'The base commit must be the documentation commit''s single immediate parent.'
    }
    if (!(Test-HandoffAncestor $DocumentationCommit $sourceTip)) {
        throw 'The documentation commit is not reachable from the pushed source branch.'
    }
    if (Test-HandoffAncestor $DocumentationCommit $defaultTip) {
        throw 'The documentation commit is already reachable from the default branch; investigate delivery sequencing before proceeding.'
    }
    $diff = (Invoke-HandoffGit @('diff', '--name-only', '--no-renames', '-z', $BaseCommit, $DocumentationCommit, '--')) -join "`n"
    $changedPaths = @($diff.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
    if ($changedPaths.Count -eq 0) { throw 'A documentation handoff requires a non-empty Markdown-only commit.' }
    foreach ($path in $changedPaths) {
        if (!$path.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The documentation commit must change only Markdown files.'
        }
    }
    if ($changedPaths.Count -ne $expectedPaths.Count -or @($changedPaths | Where-Object { !$expectedPaths.Contains($_) }).Count) {
        throw 'The handoff must list the exact changed Markdown paths, using repository-relative Git paths.'
    }
    $verification = [pscustomobject][ordered]@{
        SourceBranch = $SourceBranch; SourceTip = $sourceTip
        BaseCommit = $BaseCommit; DocumentationCommit = $DocumentationCommit
        MarkdownFiles = $changedPaths; DefaultBranch = $DefaultBranch; DefaultTip = $defaultTip
    }
} finally {
    $null = Invoke-HandoffGit @('update-ref', '-d', $sourceRef)
    $null = Invoke-HandoffGit @('update-ref', '-d', $defaultRef)
}
$verification
