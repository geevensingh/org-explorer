$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$checker = Join-Path $repository 'eng\Test-TestingDocumentation.ps1'
$fixture = Join-Path $repository ('.test-workspaces\testing-documentation-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path (Join-Path $fixture 'docs\testing')
& git -C $fixture init --quiet
if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize isolated Markdown fixture.' }

function Write-Fixture([string] $Path, [string] $Text) {
    [IO.File]::WriteAllText((Join-Path $fixture $Path), $Text)
}

$entryText = @'
# Testing

## Placement
### Retired-engine coverage reconciliation (#100)
[Storage](testing/workspace-storage-recovery.md#retired-engine-coverage-reconciliation-100)

## Moving coverage
## Required PR/main execution
[CI](testing/ci-and-artifacts.md#required-prmain-execution)

## Real desktop execution
[Harness](testing/desktop-harness.md#running-required-desktop-suites)

## Separate scale commands and results
[Scale](testing/scale-verification.md#separate-scale-commands-and-results)

## Nightly and manual execution
[Manual](testing/release-and-manual.md#nightly-and-manual-execution)

## Active pre-publication integration
[Release](testing/release-and-manual.md#active-pre-publication-integration)

## Isolation and remaining blockers
'@
$entryText += "`n"

function Reset-Fixture {
    Write-Fixture 'docs\testing.md' $entryText
    Write-Fixture 'docs\testing\ci-and-artifacts.md' "# CI`n[Testing](../testing.md)`n## Required PR/main execution`n"
    Write-Fixture 'docs\testing\desktop-harness.md' "# Harness`n[Testing](../testing.md)`n## Running required desktop suites`n"
    Write-Fixture 'docs\testing\workspace-storage-recovery.md' "# Storage`n[Testing](../testing.md)`n## Retired-engine coverage reconciliation (#100)`n"
    Write-Fixture 'docs\testing\scale-verification.md' "# Scale`n[Testing](../testing.md)`n## Separate scale commands and results`n## Required smoke`n"
    Write-Fixture 'docs\testing\release-and-manual.md' "# Release`n[Testing](../testing.md)`n## Nightly and manual execution`n## Active pre-publication integration`n"
    & git -c core.autocrlf=false -C $fixture add -- .
    if ($LASTEXITCODE -ne 0) { throw 'Cannot track isolated Markdown fixture.' }
}

function Invoke-Check([string] $Failure = '', [switch] $ProcessBoundary) {
    if (!$ProcessBoundary) {
        $failed = $false
        try { & $checker -RepositoryPath $fixture | Out-Null }
        catch {
            if (!$Failure -or !$_.Exception.Message.Contains($Failure)) { throw }
            $failed = $true
        }
        if ($Failure -and !$failed) { throw "Expected failing diagnostic '$Failure'." }
        return
    }
    $start = [Diagnostics.ProcessStartInfo]::new([Environment]::ProcessPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', $checker, '-RepositoryPath', $fixture)) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    try {
        if (!$process.WaitForExit(10000)) {
            $process.Kill($true)
            throw 'Offline documentation fixture exceeded ten seconds.'
        }
        $text = $output.GetAwaiter().GetResult() + $errors.GetAwaiter().GetResult()
        $plain = [regex]::Replace($text, '\x1b\[[0-9;]*m', '')
        $diagnostic = [regex]::Replace([regex]::Replace($plain, '(?m)^\s*\|\s?', ''), '\s+', ' ')
        if ($Failure) {
            if ($process.ExitCode -eq 0 -or !$diagnostic.Contains($Failure)) {
                throw "Expected failing diagnostic '$Failure'; exit $($process.ExitCode): $text"
            }
        } elseif ($process.ExitCode -ne 0) {
            throw "Valid documentation graph failed: $text"
        }
    } finally { $process.Dispose() }
}

try {
    Reset-Fixture
    $padding = 300 - [IO.File]::ReadAllLines((Join-Path $fixture 'docs\testing.md')).Length
    Write-Fixture 'docs\testing.md' ($entryText + ("`n" * $padding))
    Invoke-Check -ProcessBoundary
    Write-Fixture 'docs\testing.md' ($entryText + ("`n" * ($padding + 1)))
    Invoke-Check 'docs\testing.md: 301 physical lines exceeds 300' -ProcessBoundary
    Write-Host 'PASS: exactly 300 physical entry lines succeeds; 301 fails with the source and budget.'

    Reset-Fixture
    Invoke-Check
    Write-Fixture 'docs\testing.md' ($entryText + "[Missing](testing/missing.md)`n")
    Invoke-Check 'docs\testing.md:24: missing local file: testing/missing.md'
    Write-Host 'PASS: real local destinations and reference return links are checked without executing examples.'

    Write-Fixture 'docs\testing.md' ($entryText + "[Smoke](testing/scale-verification.md#required-smoke)`n")
    Invoke-Check
    Write-Fixture 'docs\testing.md' ($entryText + "[Absent](testing/scale-verification.md#absent)`n")
    Invoke-Check 'docs\testing.md:24: unresolved fragment: testing/scale-verification.md#absent'
    Write-Host 'PASS: local heading fragments resolve against actual destination content.'

    Write-Fixture 'docs\testing.md' ($entryText.Replace(
        'testing/scale-verification.md#separate-scale-commands-and-results',
        'testing/desktop-harness.md#running-required-desktop-suites') + "[Scale index](testing/scale-verification.md)`n")
    Invoke-Check 'docs\testing.md: compatibility section #separate-scale-commands-and-results must forward to testing/scale-verification.md#separate-scale-commands-and-results'
    Write-Host 'PASS: a resolving but wrong compatibility destination cannot replace the owning contract.'

    Reset-Fixture
    $fencedEntry = $entryText + @'

````markdown
## Fenced heading
[Not navigation](testing/missing.md)
```
throw 'This documented example must never execute.'
````
   ~~~markdown
[Also not navigation](testing/absent.md)
   ~~~
Inline `[Example](testing/missing.md)` is not a link.
<!-- [Comment](testing/missing.md) -->
[External](https://invalid.example.test/never-contact)
'@
    Write-Fixture 'docs\testing.md' $fencedEntry
    Invoke-Check
    Write-Fixture 'docs\testing.md' ($fencedEntry + "`n[Not an anchor](#fenced-heading)`n")
    Invoke-Check 'unresolved fragment: #fenced-heading'
    Write-Host 'PASS: fenced/inline examples, comments and external URLs are not local navigation or execution.'

    Reset-Fixture
    Write-Fixture 'docs\testing\scale-verification.md' @'
# Scale
[Testing](../testing.md)
## Separate scale commands and results
## Same heading
## Same heading
## Same heading-1
## Same heading
'@
    Write-Fixture 'docs\testing.md' ($entryText + @'
[Second](testing/scale-verification.md#same-heading-1)
[Collision](testing/scale-verification.md#same-heading-1-1)
[Fourth](testing/scale-verification.md#same-heading-2)
'@)
    Invoke-Check
    Write-Fixture 'docs\testing.md' ($entryText + "[Absent duplicate](testing/scale-verification.md#same-heading-3)`n")
    Invoke-Check 'unresolved fragment: testing/scale-verification.md#same-heading-3'
    Write-Host 'PASS: duplicate headings use collision-safe GitHub fragments rather than invented anchors.'

    Reset-Fixture
    Write-Fixture 'docs\testing.md' ($entryText.Replace(
        'testing/scale-verification.md#separate-scale-commands-and-results',
        './testing/scale-verification.md#separate-scale-commands-and-results'))
    Invoke-Check
    Write-Host 'PASS: equivalent local forwarding paths are navigation, not a Markdown style constraint.'

    Reset-Fixture
    Write-Fixture 'README.md' "[Old scale link](docs/testing.md#separate-scale-commands-and-results)`n[Unrelated](../../outside.md)`n"
    Write-Fixture 'docs\supporting.md' "# Supporting`n[Not crawled](missing-outside-graph.md)`n"
    Write-Fixture 'docs\testing.md' ($entryText + "[Supporting](supporting.md#supporting)`n")
    & git -c core.autocrlf=false -C $fixture add -- .
    if ($LASTEXITCODE -ne 0) { throw 'Cannot track isolated inbound fixture.' }
    Invoke-Check
    Write-Fixture 'README.md' "[Broken old link](docs/testing.md#absent)`n"
    Invoke-Check 'README.md:1: unresolved fragment: docs/testing.md#absent'
    Write-Host 'PASS: affected inbound fragments are checked; supporting/unrelated navigation is not crawled.'

    Write-Fixture 'docs\testing\current-topic.md' "# Current topic`n[Testing](../testing.md)`n"
    Write-Fixture 'docs\testing.md' ($entryText + "[Current topic](testing/current-topic.md)`n")
    Write-Fixture 'README.md' "[Old topic](docs/testing/old-topic.md)`n"
    Invoke-Check 'README.md:1: missing local file: docs/testing/old-topic.md'
    Remove-Item -LiteralPath (Join-Path $fixture 'docs\testing\current-topic.md')
    Write-Host 'PASS: stale inbound links to missing Testing references fail even when the router points to the current reference.'

    Reset-Fixture
    Write-Fixture 'README.md' ''
    Write-Fixture 'docs\testing.md' ($entryText + "[Outside](../../outside.md)`n")
    Invoke-Check 'docs\testing.md:24: ../../outside.md: destination leaves repository'
    Write-Host 'PASS: owned navigation cannot inspect destinations outside the repository.'

    Reset-Fixture
    Write-Fixture 'docs\testing\scale-verification.md' "# Scale`n## Separate scale commands and results`n"
    Invoke-Check 'docs\testing\scale-verification.md: missing return link to docs\testing.md'
    Reset-Fixture
    Write-Fixture 'docs\testing\orphan.md' "# Orphan`n[Testing](../testing.md)`n"
    Invoke-Check 'docs\testing\orphan.md: reference is unreachable from docs\testing.md'
    Remove-Item -LiteralPath (Join-Path $fixture 'docs\testing\orphan.md')
    Reset-Fixture
    Write-Fixture 'docs\testing.md' ($entryText.Replace('## Moving coverage', '## Renamed coverage'))
    Invoke-Check 'docs\testing.md: missing original anchor #moving-coverage'
    Write-Host 'PASS: references require reachability and return links, and original entry anchors stay available.'

    & $checker -RepositoryPath $repository
    Write-Host 'PASS: the complete real Testing graph and affected tracked inbound links satisfy the same contract.'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
