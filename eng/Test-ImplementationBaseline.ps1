[CmdletBinding(DefaultParameterSetName = 'Remote')]
param(
    [Parameter(Mandatory)][string]$BaseBranch,
    [string]$RepositoryPath = '.',
    [Parameter(ParameterSetName = 'Remote')][string]$Remote = 'origin',
    [Parameter(Mandatory, ParameterSetName = 'Local')][switch]$LocalBase,
    [Parameter(Mandatory, ParameterSetName = 'Local')][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedBaseRevision
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-BaselineGit([string[]]$Arguments) {
    $output = & git -C $RepositoryPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Baseline Git command failed: git $($Arguments -join ' ')." }
    return $output
}

$null = Invoke-BaselineGit @('check-ref-format', "refs/heads/$BaseBranch")
$branch = Invoke-BaselineGit @('symbolic-ref', '--quiet', '--short', 'HEAD')
$startingSha = Invoke-BaselineGit @('rev-parse', '--verify', 'HEAD')
Write-Host "Starting branch: $branch; SHA: $startingSha"
foreach ($operation in @('MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'rebase-merge', 'rebase-apply', 'sequencer', 'BISECT_START')) {
    $path = Invoke-BaselineGit @('rev-parse', '--path-format=absolute', '--git-path', $operation)
    if (Test-Path -LiteralPath $path) { throw "Finish or explicitly abort the in-progress $operation before implementation." }
}
if (@(Invoke-BaselineGit @('status', '--porcelain=v1', '--untracked-files=all')).Count -ne 0) {
    throw 'Implementation preflight requires a clean worktree. Preserve existing work and agree on its disposition before continuing.'
}

if ($LocalBase) {
    $baseRef = "refs/heads/$BaseBranch"
    $baseSha = Invoke-BaselineGit @('rev-parse', '--verify', "$baseRef^{commit}")
    if ($baseSha -cne $ExpectedBaseRevision) { throw 'The coordinator integration branch moved or does not match the supplied baseline. Obtain its current verified SHA.' }
} else {
    $null = Invoke-BaselineGit @('check-ref-format', "refs/remotes/$Remote/$BaseBranch")
    $url = Invoke-BaselineGit @('remote', 'get-url', $Remote)
    $github = $url -cmatch '^https://github\.com/geevensingh/org-explorer(\.git)?$'
    if (!$github -and $url -notmatch '^file://' -and !(Test-Path -LiteralPath $url)) {
        throw 'Unsupported remote. Use the HTTPS geevensingh/org-explorer origin so the verified gh account supplies fetch credentials, or a local Git fixture.'
    }
    $previousGhToken = $env:GH_TOKEN
    $previousGithubToken = $env:GITHUB_TOKEN
    try {
        if ($github) {
            $env:GH_TOKEN = ''
            $env:GITHUB_TOKEN = ''
            $token = & gh auth token --user geevensingh
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) { throw 'Cannot obtain the geevensingh GitHub token.' }
            $env:GH_TOKEN = $token
            $login = & gh api user --jq .login
            if ($LASTEXITCODE -ne 0 -or $login -cne 'geevensingh') { throw 'Baseline fetch requires the geevensingh account.' }
            $repository = & gh repo view geevensingh/org-explorer --json nameWithOwner --jq .nameWithOwner
            if ($LASTEXITCODE -ne 0 -or $repository -cne 'geevensingh/org-explorer') { throw 'Baseline repository verification failed.' }
        }
        $baseRef = "refs/remotes/$Remote/$BaseBranch"
        $fetchArguments = @('fetch', '--no-tags', '--no-recurse-submodules', '--no-write-fetch-head', $Remote, "+refs/heads/${BaseBranch}:$baseRef")
        if ($github) { $fetchArguments = @('-c', 'credential.helper=', '-c', 'credential.helper=!gh auth git-credential') + $fetchArguments }
        $null = Invoke-BaselineGit $fetchArguments
        $baseSha = Invoke-BaselineGit @('rev-parse', '--verify', "$baseRef^{commit}")
    } finally {
        $env:GH_TOKEN = $previousGhToken
        $env:GITHUB_TOKEN = $previousGithubToken
    }
}

$counts = (Invoke-BaselineGit @('rev-list', '--left-right', '--count', "HEAD...$baseSha")) -split '\s+'
$ahead = [int]$counts[0]
$behind = [int]$counts[1]
[pscustomobject]@{
    startingBranch = $branch; startingSha = $startingSha; baseRef = $baseRef; baseSha = $baseSha
    ahead = $ahead; behind = $behind; diverged = ($ahead -gt 0 -and $behind -gt 0); localBase = [bool]$LocalBase
} | ConvertTo-Json -Compress
if ($behind -gt 0) {
    throw 'Stale implementation baseline. Stop before edits; agree to update or select the explicitly intended integration/stack base, then rerun preflight.'
}
if ((Invoke-BaselineGit @('rev-parse', 'HEAD')) -cne $startingSha -or
    (Invoke-BaselineGit @('symbolic-ref', '--quiet', '--short', 'HEAD')) -cne $branch -or
    (Invoke-BaselineGit @('rev-parse', '--verify', "$baseRef^{commit}")) -cne $baseSha -or
    @(Invoke-BaselineGit @('status', '--porcelain=v1', '--untracked-files=all')).Count -ne 0) {
    throw 'Checkout or baseline changed during preflight. Inspect the worktree and rerun before implementation.'
}
