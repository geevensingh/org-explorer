$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$verifier = Join-Path $root 'eng\Verify-DocumentationHandoff.ps1'
$fixture = Join-Path $root ('.ci-results\documentation handoff-' + [guid]::NewGuid().ToString('N'))
$remote = Join-Path $fixture 'remote.git'
$author = Join-Path $fixture 'author'
$reader = Join-Path $fixture 'reader'
$checks = 0

function Assert([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function Invoke-FixtureGit([string]$Repository, [string[]]$Arguments) {
    $output = & git -C $Repository @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git failed: $($output -join "`n")" }
    return $output
}
function Commit-Fixture([string]$Message) {
    $null = Invoke-FixtureGit $author @('add', '--all')
    $null = Invoke-FixtureGit $author @('commit', '--quiet', '-m', $Message)
    return Invoke-FixtureGit $author @('rev-parse', 'HEAD')
}
function Verify-Handoff([hashtable]$Overrides = @{}) {
    $parameters = $handoff.Clone()
    foreach ($key in $Overrides.Keys) { $parameters[$key] = $Overrides[$key] }
    & $verifier @parameters
}
function Rejects([scriptblock]$Action, [string]$Expected) {
    $message = ''
    try { $null = & $Action } catch { $message = $_.Exception.Message }
    Assert ($message.Contains($Expected)) "Expected rejection containing '$Expected', got '$message'."
}

$null = New-Item -ItemType Directory -Path $fixture -Force
try {
    $null = Invoke-FixtureGit $fixture @('init', '--quiet', '--bare', '--initial-branch=main', $remote)
    $null = Invoke-FixtureGit $fixture @('init', '--quiet', '--initial-branch=main', $author)
    foreach ($entry in @(
        @('user.name', 'Fixture'), @('user.email', 'fixture@example.invalid'),
        @('commit.gpgsign', 'false'), @('core.hooksPath', (Join-Path $fixture 'no-hooks'))
    )) {
        $null = Invoke-FixtureGit $author @('config', $entry[0], $entry[1])
    }
    $null = Invoke-FixtureGit $author @('remote', 'add', 'origin', $remote)
    'Baseline' | Set-Content -LiteralPath (Join-Path $author 'README.md')
    $base = Commit-Fixture 'Baseline'
    $null = Invoke-FixtureGit $author @('push', '--quiet', 'origin', 'main')
    $null = Invoke-FixtureGit $author @('switch', '--quiet', '-c', 'planning')
    $null = New-Item -ItemType Directory -Path (Join-Path $author 'docs')
    'Approved vocabulary' | Set-Content -LiteralPath (Join-Path $author 'GLOSSARY.md')
    'Approved design' | Set-Content -LiteralPath (Join-Path $author 'docs\team design.md')
    $doc = Commit-Fixture 'Approved documentation'
    $null = Invoke-FixtureGit $author @('push', '--quiet', 'origin', 'planning')
    $null = Invoke-FixtureGit $fixture @('clone', '--quiet', '--no-local', $remote, $reader)
    $handoff = @{
        RepositoryPath = $reader; SourceBranch = 'planning'; DefaultBranch = 'main'
        BaseCommit = $base; DocumentationCommit = $doc
        MarkdownFiles = @('GLOSSARY.md', 'docs/team design.md')
    }
    'Unsaved local draft' | Set-Content -LiteralPath (Join-Path $reader 'README.md')
    'Untracked local writing' | Set-Content -LiteralPath (Join-Path $reader 'draft.txt')
    $beforeStatus = @(Invoke-FixtureGit $reader @('status', '--porcelain=v1')) -join "`n"
    $beforeHead = Invoke-FixtureGit $reader @('rev-parse', 'HEAD')
    $beforeRefs = @(Invoke-FixtureGit $reader @('for-each-ref', '--format=%(refname) %(objectname)')) -join "`n"

    $initial = Verify-Handoff
    Assert ($initial.SourceTip -ceq $doc -and $initial.DocumentationCommit -ceq $doc) 'A source tip equal to the documentation commit must verify.'
    Assert ($initial.BaseCommit -ceq $base -and $initial.MarkdownFiles.Count -eq 2) 'The verified result must preserve the immutable base and exact Markdown paths.'

    'Implementation after the approved design' | Set-Content -LiteralPath (Join-Path $author 'implementation.txt')
    $advanced = Commit-Fixture 'Advance the source branch'
    $null = Invoke-FixtureGit $author @('push', '--quiet', 'origin', 'planning')
    $result = Verify-Handoff @{ MarkdownFiles = @('docs/team design.md', 'GLOSSARY.md') }
    Assert ($result.SourceTip -ceq $advanced -and $result.DocumentationCommit -ceq $doc) 'A freshly fetched advanced branch must retain the approved documentation SHA rather than reject or replace it.'
    Assert ((Invoke-FixtureGit $reader @('rev-parse', 'origin/planning')) -ceq $doc) 'Verification must use fresh remote refs without rewriting existing tracking refs.'

    Rejects { Verify-Handoff @{ BaseCommit = $advanced } } 'immediate parent'
    Rejects { Verify-Handoff @{ MarkdownFiles = @('GLOSSARY.md') } } 'exact changed Markdown paths'
    Rejects { Verify-Handoff @{ MarkdownFiles = @('GLOSSARY.md', 'GLOSSARY.md') } } 'Duplicate Markdown path'

    $null = Invoke-FixtureGit $author @('switch', '--quiet', '-c', 'mixed', $base)
    'Design' | Set-Content -LiteralPath (Join-Path $author 'mixed.md')
    'Code' | Set-Content -LiteralPath (Join-Path $author 'mixed.cs')
    $mixed = Commit-Fixture 'Mixed documentation and code'
    $null = Invoke-FixtureGit $author @('push', '--quiet', 'origin', 'mixed')
    Rejects { Verify-Handoff @{ SourceBranch = 'mixed'; DocumentationCommit = $mixed; MarkdownFiles = @('mixed.md') } } 'only Markdown'

    $null = Invoke-FixtureGit $author @('switch', '--quiet', '-c', 'empty', $base)
    $null = Invoke-FixtureGit $author @('commit', '--quiet', '--allow-empty', '-m', 'Empty documentation')
    $empty = Invoke-FixtureGit $author @('rev-parse', 'HEAD')
    $null = Invoke-FixtureGit $author @('push', '--quiet', 'origin', 'empty')
    Rejects { Verify-Handoff @{ SourceBranch = 'empty'; DocumentationCommit = $empty } } 'non-empty'

    $null = Invoke-FixtureGit $author @('push', '--quiet', '--force', 'origin', "${base}:refs/heads/planning")
    Rejects { Verify-Handoff } 'not reachable from the pushed source branch'
    $null = Invoke-FixtureGit $author @('push', '--quiet', '--force', 'origin', "${advanced}:refs/heads/planning")
    Rejects { Verify-Handoff @{ DefaultBranch = 'missing' } } 'Git fetch failed'

    $null = Invoke-FixtureGit $author @('push', '--quiet', 'origin', "${doc}:refs/heads/main")
    Rejects { Verify-Handoff } 'already reachable from the default branch'

    Assert (((@(Invoke-FixtureGit $reader @('status', '--porcelain=v1')) -join "`n") -ceq $beforeStatus) -and
        (Get-Content -LiteralPath (Join-Path $reader 'README.md') -Raw).Trim() -ceq 'Unsaved local draft') 'Verification must preserve tracked and untracked local writing.'
    Assert ((Invoke-FixtureGit $reader @('rev-parse', 'HEAD')) -ceq $beforeHead) 'Verification must not change the checkout.'
    Assert ((@(Invoke-FixtureGit $reader @('for-each-ref', '--format=%(refname) %(objectname)')) -join "`n") -ceq $beforeRefs) 'Successful and rejected verification must clean up temporary refs without changing existing refs.'
    Write-Host "PASS: $checks documentation-handoff checks using real local Git remotes."
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
