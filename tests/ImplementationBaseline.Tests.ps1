$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptPath = (Resolve-Path "$PSScriptRoot\..\eng\Test-ImplementationBaseline.ps1").Path
$root = Join-Path $PSScriptRoot ("..\.test-workspaces\implementation-baseline-" + [guid]::NewGuid().ToString('N'))
$origin = Join-Path $root 'origin.git'
$seed = Join-Path $root 'seed'
$client = Join-Path $root 'client'
$checks = 0
$git = (Get-Command git -CommandType Application | Select-Object -First 1).Source

function Git([string]$Repository, [string[]]$Arguments) {
    $output = & $git -C $Repository @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git failed: $($Arguments -join ' ')." }
    return $output
}
function Assert($Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}
function Commit([string]$Repository, [string]$File, [string]$Text) {
    Set-Content -LiteralPath (Join-Path $Repository $File) -Value $Text
    $null = Git $Repository @('add', '--', $File)
    $null = Git $Repository @('-c', 'user.name=Baseline Test', '-c', 'user.email=baseline@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', $Text)
    return Git $Repository @('rev-parse', 'HEAD')
}
function Check([string]$Name, [string[]]$Arguments, [string]$Failure = '', $Expected = $null, [string]$Repository = $client, [switch]$WithoutFixtureOptIn) {
    $before = Git $Repository @('rev-parse', 'HEAD')
    $status = @(Git $Repository @('status', '--porcelain=v1'))
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if (!$WithoutFixtureOptIn -and $Arguments -notcontains '-LocalBase') { $Arguments += '-AllowLocalFixture' }
    foreach ($argument in (@('-NoProfile', '-NonInteractive', '-File', $scriptPath, '-RepositoryPath', $Repository) + $Arguments)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    try {
        if (!$process.WaitForExit(30000)) { $process.Kill($true); $process.WaitForExit(); throw "$Name timed out." }
        $text = $output.GetAwaiter().GetResult()
        $errorText = $errors.GetAwaiter().GetResult()
        Assert (!$text.Contains('fixture-authorization-value') -and !$errorText.Contains('fixture-authorization-value')) "$Name exposed an Authorization header."
        if ($Failure) {
            Assert ($process.ExitCode -ne 0 -and $errorText.Contains($Failure)) "$Name did not fail as expected: $text $errorText"
        } else {
            Assert ($process.ExitCode -eq 0) "$Name failed: $text $errorText"
        }
        if ($null -ne $Expected) {
            $json = @($text -split '\r?\n' | Where-Object { $_.StartsWith('{') })
            Assert ($json.Count -eq 1) "$Name did not produce one baseline report: $text"
            $report = $json[0] | ConvertFrom-Json
            foreach ($key in $Expected.Keys) { Assert ($report.$key -ceq $Expected[$key]) "$Name reported wrong $key : $text" }
        }
        Assert ((Git $Repository @('rev-parse', 'HEAD')) -ceq $before) "$Name changed the checkout tip."
        Assert ((@(Git $Repository @('status', '--porcelain=v1')) -join "`n") -ceq ($status -join "`n")) "$Name changed worktree contents."
        $script:checks++
        Write-Host "PASS: $Name"
    } finally { $process.Dispose() }
}

New-Item $root -ItemType Directory -Force | Out-Null
try {
    $null = Git $root @('init', '--quiet', '--bare', $origin)
    $null = Git $root @('init', '--quiet', '--initial-branch=main', $seed)
    $initial = Commit $seed 'behavior.txt' 'current behavior'
    $null = Git $seed @('remote', 'add', 'origin', $origin)
    $null = Git $seed @('push', '--quiet', 'origin', 'main')
    $null = Git $root @('clone', '--quiet', '--branch', 'main', $origin, $client)
    Check 'implicit local mirror is not an authoritative remote' @('-BaseBranch', 'main') 'Unsupported remote' -WithoutFixtureOptIn
    $null = Git $client @('remote', 'set-url', 'origin', ([Uri]::new($origin).AbsoluteUri))
    Check 'implicit file URL mirror is not an authoritative remote' @('-BaseBranch', 'main') 'Unsupported remote' -WithoutFixtureOptIn
    Check 'explicit file URL fixture' @('-BaseBranch', 'main') -Expected @{ baseSha = $initial; ahead = 0; behind = 0 }
    $null = Git $client @('remote', 'set-url', 'origin', 'https://github.com/geevensingh/org-explorer.git')
    foreach ($key in @('http.extraheader', 'http.https://github.com/.extraheader', 'http.https://github.com/geevensingh/org-explorer.git.extraheader')) {
        $null = Git $client @('config', '--local', '--add', $key, 'aUtHoRiZaTiOn: Basic fixture-authorization-value')
        $null = Git $client @('config', '--local', '--add', $key, 'X-Fixture: safe')
        Check "reject configured Authorization in $key before authentication or fetch" @('-BaseBranch', 'main') 'Configured HTTP Authorization extraheaders' -WithoutFixtureOptIn
        $null = Git $client @('config', '--local', '--unset-all', $key)
    }
    $null = Git $client @('remote', 'set-url', 'origin', $origin)
    Check 'fresh remote main' @('-BaseBranch', 'main') -Expected @{
        startingBranch = 'main'; startingSha = $initial; baseRef = 'refs/remotes/origin/main'; baseSha = $initial
        ahead = 0; behind = 0; diverged = $false; localBase = $false
    }

    $upstream = Commit $seed 'behavior.txt' 'upstream behavior supersedes old spec'
    $null = Git $seed @('push', '--quiet', 'origin', 'main')
    Check 'stale local main is not the baseline' @('-BaseBranch', 'main') 'Stale implementation baseline' @{
        startingSha = $initial; baseSha = $upstream; ahead = 0; behind = 1; diverged = $false
    }
    Assert ((Git $client @('rev-parse', 'refs/heads/main')) -ceq $initial) 'Preflight updated local main.'
    Assert ((Git $client @('rev-parse', 'refs/remotes/origin/main')) -ceq $upstream) 'Preflight did not fetch current main.'
    $null = Commit $client 'local.txt' 'independent work'
    Check 'diverged work is preserved and rejected' @('-BaseBranch', 'main') 'Stale implementation baseline' @{
        baseSha = $upstream; ahead = 1; behind = 1; diverged = $true
    }
    $null = Git $client @('switch', '--quiet', '--create', 'fresh', 'origin/main')
    Check 'fresh feature branch' @('-BaseBranch', 'main') -Expected @{ startingBranch = 'fresh'; baseSha = $upstream; ahead = 0; behind = 0 }
    $null = Commit $client 'feature.txt' 'new feature'
    Check 'ahead-only implementation commits' @('-BaseBranch', 'main') -Expected @{ ahead = 1; behind = 0; diverged = $false }

    $null = Git $seed @('switch', '--quiet', '--create', 'integration/pushed')
    $integration = Commit $seed 'integration.txt' 'approved integration baseline'
    $null = Git $seed @('push', '--quiet', 'origin', 'integration/pushed')
    Check 'wrong ticket baseline cannot pass integration gate' @('-BaseBranch', 'integration/pushed') 'Stale implementation baseline' @{
        baseSha = $integration; ahead = 1; behind = 1; diverged = $true
    }
    $null = Git $client @('switch', '--quiet', '--create', 'ticket/pushed', 'origin/integration/pushed')
    $null = Commit $client 'ticket.txt' 'ticket implementation'
    Check 'explicit remote integration or stack' @('-BaseBranch', 'integration/pushed') -Expected @{
        baseRef = 'refs/remotes/origin/integration/pushed'; baseSha = $integration; ahead = 1; behind = 0
    }
    $integrationNext = Commit $seed 'integration.txt' 'next integration baseline'
    $null = Git $seed @('push', '--quiet', 'origin', 'integration/pushed')
    Check 'advanced remote integration invalidates ticket baseline' @('-BaseBranch', 'integration/pushed') 'Stale implementation baseline' @{
        baseSha = $integrationNext; ahead = 1; behind = 1; diverged = $true
    }

    $null = Git $client @('switch', '--quiet', '--create', 'integration/local', 'origin/main')
    $local = Commit $client 'local-integration.txt' 'unpushed coordinator baseline'
    $ticketWorktree = Join-Path $root 'ticket-worktree'
    $null = Git $client @('worktree', 'add', '--quiet', '-b', 'ticket/local', $ticketWorktree, 'integration/local')
    Check 'linked ticket worktree includes exact unpushed integration baseline' @('-BaseBranch', 'integration/local', '-LocalBase', '-ExpectedBaseRevision', $local) -Repository $ticketWorktree -Expected @{
        startingBranch = 'ticket/local'; baseRef = 'refs/heads/integration/local'; baseSha = $local; ahead = 0; behind = 0; localBase = $true
    }
    Check 'wrong coordinator revision' @('-BaseBranch', 'integration/local', '-LocalBase', '-ExpectedBaseRevision', $initial) 'coordinator integration branch moved' -Repository $ticketWorktree
    $localNext = Commit $client 'local-integration.txt' 'coordinator advanced'
    Check 'moved coordinator branch invalidates pinned worktree baseline' @('-BaseBranch', 'integration/local', '-LocalBase', '-ExpectedBaseRevision', $local) 'coordinator integration branch moved' -Repository $ticketWorktree
    Check 'linked ticket worktree missing advanced baseline' @('-BaseBranch', 'integration/local', '-LocalBase', '-ExpectedBaseRevision', $localNext) 'Stale implementation baseline' @{
        startingSha = $local; baseSha = $localNext; ahead = 0; behind = 1
    } -Repository $ticketWorktree
    Check 'missing local baseline SHA' @('-BaseBranch', 'integration/local', '-LocalBase') 'ExpectedBaseRevision'
    $null = Git $client @('switch', '--quiet', '--create', 'ticket/stale', 'origin/main')

    Check 'missing remote branch' @('-BaseBranch', 'does-not-exist') 'Baseline Git command failed'
    Check 'invalid base ref' @('-BaseBranch', 'main:other') 'Baseline Git command failed'
    $notRepository = Join-Path $root 'not-a-repository'
    New-Item $notRepository -ItemType Directory | Out-Null
    $null = Git $client @('remote', 'set-url', 'origin', $notRepository)
    Check 'fetch failure rejects cached successful baseline' @('-BaseBranch', 'main') 'Baseline Git command failed'
    $null = Git $client @('remote', 'set-url', 'origin', 'git@github.com:geevensingh/org-explorer.git')
    Check 'SSH cannot bypass verified fetch credentials' @('-BaseBranch', 'main') 'Unsupported remote'
    $null = Git $client @('remote', 'set-url', 'origin', 'https://github.com/geevens_microsoft/org-explorer.git')
    Check 'another repository cannot use this account route' @('-BaseBranch', 'main') 'Unsupported remote'
    $null = Git $client @('remote', 'set-url', 'origin', $origin)

    Set-Content -LiteralPath (Join-Path $client 'untracked.txt') 'unsaved work'
    Check 'untracked work requires disposition' @('-BaseBranch', 'main') 'requires a clean worktree'
    Assert ((Get-Content -LiteralPath (Join-Path $client 'untracked.txt') -Raw).Trim() -ceq 'unsaved work') 'Preflight changed untracked writing.'
    Remove-Item -LiteralPath (Join-Path $client 'untracked.txt')
    Set-Content -LiteralPath (Join-Path $client 'behavior.txt') 'dirty tracked work'
    Check 'tracked work requires disposition' @('-BaseBranch', 'main') 'requires a clean worktree'
    Assert ((Get-Content -LiteralPath (Join-Path $client 'behavior.txt') -Raw).Trim() -ceq 'dirty tracked work') 'Preflight changed tracked writing.'
    $null = Commit $client 'behavior.txt' 'conflicting ticket behavior'
    $null = Git $seed @('switch', '--quiet', 'main')
    $null = Commit $seed 'behavior.txt' 'conflicting upstream behavior'
    $null = Git $seed @('push', '--quiet', 'origin', 'main')
    $null = Git $client @('fetch', '--quiet', 'origin')
    & $git -C $client -c user.name='Baseline Test' -c user.email=baseline@example.invalid merge origin/main 2>&1 | Out-Null
    Assert ($LASTEXITCODE -ne 0) 'Expected a real merge conflict.'
    Check 'in-progress merge blocks implementation' @('-BaseBranch', 'main') 'in-progress MERGE_HEAD'
    $null = Git $client @('merge', '--abort')
    $null = Git $client @('switch', '--quiet', '--detach')
    Check 'detached HEAD is rejected' @('-BaseBranch', 'main') 'Baseline Git command failed'
    Assert (@(Git $client @('worktree', 'list', '--porcelain') | Where-Object { $_ -eq 'branch refs/heads/ticket/local' }).Count -eq 1) 'Preflight changed the linked ticket branch.'
    Write-Host "PASS: $checks implementation baseline checks using real local Git repositories and remotes; no network or desktop."
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
