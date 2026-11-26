$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('.ci-results\hosted-cleanup-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$startProcess = Get-Command Start-Process -CommandType Cmdlet
$holder = $null
try {
    $nativeBuild = Join-Path $fixture 'native-build'
    & dotnet build "$root\tests\OrgExplorer.Storage.ProcessFixture" --artifacts-path $nativeBuild `
        -c Release -p:RestoreLockedMode=true --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'The controlled native fixture could not be built.' }
    $native = @(Get-ChildItem -LiteralPath $nativeBuild -Filter e_sqlite3.dll -Recurse |
        Where-Object { $_.FullName.EndsWith('\runtimes\win-x64\native\e_sqlite3.dll') })[0]
    if (!$native) { throw 'The controlled fixture requires a real compiled x64 SQLite native image.' }
    $source = Join-Path $fixture 'source'
    New-Item -ItemType Directory -Path "$source\eng", "$source\tests" | Out-Null
    Copy-Item -Path "$root\eng\*.ps1" -Destination "$source\eng"
    Copy-Item -LiteralPath "$root\tests\OrgExplorer.Storage.ProcessFixture" -Destination "$source\tests" -Recurse
    '{"version":"ci-timing-v4"}' | Set-Content -LiteralPath "$source\eng\timing-policy.json"
    '.results/' | Set-Content -LiteralPath "$source\.gitignore"
    & git -C $source init --quiet -b main
    & git -C $source add .
    & git -C $source -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Controlled policy source'
    if ($LASTEXITCODE -ne 0) { throw 'The isolated policy source could not be committed.' }
    $revision = (& git -C $source rev-parse HEAD).Trim()
    'Separate immutable workflow controller' | Set-Content -LiteralPath "$source\controller.txt"
    & git -C $source add controller.txt
    & git -C $source -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Controller B'
    $controllerRevision = (& git -C $source rev-parse HEAD).Trim()
    & git -C $source checkout --quiet $revision
    $ghExecutable = @(Get-Command gh -CommandType Application)[0].Source
    $trust = Join-Path $fixture 'fixture-root.json'
    function gh {
        $global:LASTEXITCODE = 0
        if ($args[0] -cne 'api') { throw 'Unexpected external GitHub operation.' }
        switch -Regex ($args[1]) {
            '/actions/runs/321/attempts/1$' {
                @{ id = 321; run_attempt = 1; head_sha = $controllerRevision; path = '.github/workflows/scale-verification.yml'
                    head_repository = @{ full_name = 'geevensingh/org-explorer' }; referenced_workflows = @() } |
                    ConvertTo-Json -Depth 10
            }
            '/git/trees/(?<sha>[0-9a-f]{40})\?recursive=1$' {
                $entry = (& git -C $source ls-tree $Matches.sha -- eng/timing-policy.json) -split '\s+'
                if ($LASTEXITCODE -ne 0 -or $entry.Count -lt 4) { throw 'Fixture source unavailable.' }
                @{ truncated = $false; tree = @(@{ path = 'eng/timing-policy.json'; type = 'blob'; sha = $entry[2] }) } |
                    ConvertTo-Json -Depth 10
            }
            '/git/blobs/(?<sha>[0-9a-f]{40})$' {
                $text = (& git -C $source cat-file blob $Matches.sha) -join "`n"
                if ($LASTEXITCODE -ne 0) { throw 'Fixture source blob unavailable.' }
                @{ encoding = 'base64'; content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text)) } |
                    ConvertTo-Json
            }
            default { throw "Unexpected external GitHub endpoint: $($args[1])" }
        }
    }
    function Start-Process {
        param([string]$FilePath, [object[]]$ArgumentList, [switch]$PassThru, [switch]$NoNewWindow,
            [string]$RedirectStandardOutput, [string]$RedirectStandardError)
        if ($FilePath -ceq 'gh') {
            $ArgumentList += @('--custom-trusted-root', "`"$trust`"")
            $FilePath = $ghExecutable
        }
        & $startProcess -FilePath $FilePath -ArgumentList $ArgumentList -PassThru:$PassThru `
            -NoNewWindow:$NoNewWindow -RedirectStandardOutput $RedirectStandardOutput -RedirectStandardError $RedirectStandardError
    }
    . "$source\eng\PerformanceSuite.ps1"
    . "$source\eng\TimingPolicy.ps1"
    . "$source\eng\ScaleRunner.ps1"
    . "$source\eng\NonUiCohort.ps1"
    $results = Join-Path $source '.results\hosted'
    $release = Join-Path $fixture 'release-native'
    $ready = Join-Path $fixture 'native-ready'
    $nativeHolder = Join-Path $fixture 'native-holder.ps1'
    @'
param([string]$Native, [string]$Ready, [string]$Release)
$ErrorActionPreference = 'Stop'
$handle = [Runtime.InteropServices.NativeLibrary]::Load($Native)
try {
    [IO.File]::WriteAllText($Ready, $PID.ToString())
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    while (!(Test-Path -LiteralPath $Release)) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Controlled native holder expired.' }
        Start-Sleep -Milliseconds 20
    }
} finally { [Runtime.InteropServices.NativeLibrary]::Free($handle) }
'@ | Set-Content -LiteralPath $nativeHolder
    $selection = @{
        schemaVersion = 1; filter = $PerformanceFilter; requiredTests = $PerformanceTests
        invocationId = [guid]::NewGuid().ToString('N'); runId = '321'; runAttempt = '1'
        policyVersion = 'non-ui-original-v1'; configurationErrors = @()
        cohortId = "$revision-321-1"; sample = 1
        configuration = @{ workload = 'OrgExplorer.Persistence.Tests'; testTree = 'b' * 40; fixtureTree = 'c' * 40
            sdk = '10.0.404'; runtimes = @('Microsoft.NETCore.App 10.0.0'); os = 'Windows'; architecture = 'X64'
            processors = 4; image = 'win25'; imageVersion = '20261126'; powershell = '7.6'
            setup = 'locked-restore;isolated-build;setup-and-cleanup-included' }
    }
    $signing = Join-Path $fixture 'external-attestation-action.ps1'
    @'
param([string]$Proof, [string]$Trust, [string]$Factory, [string]$Revision, [string]$ReplayBundle, [int]$DelaySeconds,
    [string]$Hosting = 'github-hosted')
$ErrorActionPreference = 'Stop'
Add-Type -Path $Factory -ReferencedAssemblies System.Security.Cryptography.Pkcs,System.Security.Cryptography,System.Formats.Asn1,System.Text.Json,System.Runtime,System.Runtime.Numerics,System.Collections
$directory = Split-Path $Proof -Parent
$watcher = [IO.FileSystemWatcher]::new($directory, 'cleanup-proof.json')
try {
    $watcher.EnableRaisingEvents = $true
    if (!(Test-Path -LiteralPath $Proof)) {
        $event = $watcher.WaitForChanged(([IO.WatcherChangeTypes]::Created -bor [IO.WatcherChangeTypes]::Renamed), 30000)
        if ($event.TimedOut) { throw 'External fixture received no proof.' }
    }
    $data = Get-Content -LiteralPath $Proof -Raw | ConvertFrom-Json
    $bundle = Join-Path $directory "$($data.runDirectory)\cleanup-attestation.json"
    if ($DelaySeconds) { Start-Sleep -Seconds $DelaySeconds }
    if ($ReplayBundle) { [IO.File]::WriteAllText("$bundle.writing", (Get-Content -LiteralPath $ReplayBundle -Raw)) }
    else { [CleanupProvenanceFixture]::Sign($Proof, "$bundle.writing", $Trust, $Revision, '321', '1', $Hosting) }
    [IO.File]::Move("$bundle.writing", $bundle)
} finally { $watcher.Dispose() }
'@ | Set-Content -LiteralPath $signing
    New-Item -ItemType Directory -Path $results -Force | Out-Null
    $signer = & $startProcess pwsh -ArgumentList @('-NoProfile', '-File', "`"$signing`"",
        "`"$results\cleanup-proof.json`"", "`"$trust`"", "`"$root\tests\CleanupProvenanceFixture.cs`"", $controllerRevision) -PassThru -NoNewWindow
    $previousHosting = @($env:GITHUB_ACTIONS, $env:RUNNER_ENVIRONMENT, $env:RUNNER_OS)
    $env:GITHUB_ACTIONS = 'true'; $env:RUNNER_ENVIRONMENT = 'github-hosted'; $env:RUNNER_OS = 'Windows'
    $failure = $null
    $case = 'hosted'
    try {
        $verify = {
                param($run, $build)
                $project = if ($case -ceq 'wrong-target') { 'OrgExplorerxPersistencexTests' } else { 'OrgExplorer.Persistence.Tests' }
                $nativePath = Join-Path $build "bin\$project\release\runtimes\win-x64\native\e_sqlite3.dll"
                New-Item -ItemType Directory -Path (Split-Path $nativePath -Parent) -Force | Out-Null
                Copy-Item -LiteralPath $native.FullName -Destination $nativePath
                Invoke-ScaleProcess restore pwsh @('-NoProfile', '-Command', 'exit 0')
                Invoke-ScaleProcess test pwsh @('-NoProfile', '-Command', 'exit 0')
                @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<Results><UnitTestResult testName="$($PerformanceTests[0])" outcome="Passed" /></Results>
<ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary>
</TestRun>
"@ | Set-Content -LiteralPath (Join-Path $run 'result.trx')
                Assert-PerformanceResults $run
                $profile = @{ samplesMs = @(140.0) * 21; p95Ms = 140.0; budgetMs = 1000
                    percentile = 'Nearest rank: sorted sample at ceil(0.95 * count), one based.' }
                @(@($NonUiCohortPhases | ForEach-Object { "SCALE END $_; outcome=Passed; ms=1.0;" });
                    'SCALE ORDINARY-PROFILE ' + ($profile | ConvertTo-Json -Compress)) |
                    Set-Content -LiteralPath (Join-Path $run 'scale-measurements.log')
                $script:holder = & $startProcess pwsh -ArgumentList @('-NoProfile', '-File', "`"$nativeHolder`"",
                    "`"$nativePath`"", "`"$ready`"", "`"$release`"") -PassThru -NoNewWindow
                $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
                while (!(Test-Path -LiteralPath $ready)) {
                    if ($script:holder.HasExited -or [DateTimeOffset]::UtcNow -ge $deadline) {
                        throw 'The controlled external native image was not held after completed workload.'
                    }
                    Start-Sleep -Milliseconds 20
                }
                if ($case -ceq 'failed-work') { throw 'Intentional required assertion failure.' }
        }
        Invoke-ScaleSuite -Suite non-ui-performance -Label 'Hosted cleanup contract' `
            -ResultsDirectory $results -ExpectedRevision $revision -ExpectedPolicyVersion ci-timing-v4 `
            -Selection $selection -Verify $verify
    } catch { $failure = $_ }
    $report = Get-Content -LiteralPath (Join-Path $results 'result.json') -Raw | ConvertFrom-Json
    if ($failure -or $report.outcome -cne 'success' -or $report.observedOutcome -cne 'success') {
        throw "Completed verification on the authenticated disposable fixture must retain a failed cleanup warning, not invalidate proof. Actual: $($report.outcome); authentication: $($report.cleanup.authenticationFailure); $($report.failure)"
    }
    if ($report.cleanup.outcome -cne 'failure' -or !$report.cleanup.warning -or
        $report.cleanup.errorType -cne 'System.UnauthorizedAccessException' -or
        $report.cleanup.phase -cne 'build-housekeeping' -or
        !$report.cleanup.path.EndsWith('\e_sqlite3.dll') -or
        $report.cleanup.remainingBuild.files.Count -lt 1) {
        throw 'The public warning lost failed cleanup, exact native refusal or remaining build metadata.'
    }
    Write-Host 'PASS: authenticated post-verification native-image refusal warns without inventing cleanup success.'
    $sample = @{ number = 1; diagnostics = @(); validation = 'incomplete' }
    Read-NonUiCohortSample $sample $results $revision "$revision-321-1" '321' '1' `
        -RequirePairedReport -ExpectedPolicyVersion ci-timing-v4
    if ($sample.validation -cne 'success' -or !$sample.runnerReport.cleanup.warning -or
        $sample.measurements.phasesMs.Count -ne 21 -or $sample.measurements.profile.samplesMs.Count -ne 21) {
        throw 'A signed warning bypassed complete raw phase/profile proof or lost failed housekeeping in retained history.'
    }
    Write-Host 'PASS: complete public result reader retains failed housekeeping and validates all raw counts with candidate A/controller B.'
    $original = Get-Content -LiteralPath (Join-Path $results 'result.json') -Raw
    foreach ($mutation in @(
        { param($data) $data.cleanup.path = 'C:\wrong\e_sqlite3.dll' },
        { param($data) $data.cleanup.errorType = 'System.IO.IOException' },
        { param($data) $data.cleanup.attestation = $null },
        { param($data) $data.verificationOutcome = 'failure' },
        { param($data) $data.executionSeconds = 660.001 },
        { param($data) $data.ceilingSeconds = 900 },
        { param($data) $data.steps[1].exitCode = 7 },
        { param($data) $data.runId = '322' }
    )) {
        $data = $original | ConvertFrom-Json -AsHashtable -DateKind String
        & $mutation $data
        $changed = $data | ConvertTo-Json -Depth 40
        [IO.File]::WriteAllText((Join-Path $results 'result.json'), $changed)
        [IO.File]::WriteAllText((Join-Path $results "$($report.runDirectory)\result.json"), $changed)
        $rejected = $false
        try {
            Read-NonUiCohortSample @{ number = 1; diagnostics = @(); validation = 'incomplete' } `
                $results $revision "$revision-321-1" '321' '1' -RequirePairedReport -ExpectedPolicyVersion ci-timing-v4
        } catch { $rejected = $true }
        if (!$rejected) { throw 'The public result reader accepted forged scope, failed work, missing signature or changed budget.' }
    }
    [IO.File]::WriteAllText((Join-Path $results 'result.json'), $original)
    [IO.File]::WriteAllText((Join-Path $results "$($report.runDirectory)\result.json"), $original)
    foreach ($legacy in @('rich-scale-original-v1', 'ci-timing-v2', 'ci-timing-v3')) {
        $rejected = $false
        try {
            Read-NonUiCohortSample @{ number = 1; diagnostics = @(); validation = 'incomplete' } `
                $results $revision "$revision-321-1" '321' '1' -RequirePairedReport -ExpectedPolicyVersion $legacy
        } catch { $rejected = $true }
        if (!$rejected) { throw 'Legacy acceptance was retroactively downgraded by an artifact policy or signature.' }
    }
    Write-Host 'PASS: public reader rejects forged scope/context, failed work, missing signatures, inflated/expired limits and legacy downgrade.'
    [IO.File]::WriteAllText($release, 'release')
    if (!$holder.WaitForExit(5000)) { throw 'Controlled external native fixture did not exit.' }
    $holder.Dispose(); $holder = $null
    if (!$signer.WaitForExit(5000)) { throw 'Controlled signing fixture did not exit.' }
    $signer.Dispose(); $signer = $null
    $replay = Join-Path $fixture 'replayed-valid-bundle.json'
    [IO.File]::WriteAllText($replay, $report.cleanup.attestation)
    $previousStarted = $env:SCALE_STARTED_AT
    foreach ($case in @('local', 'self-hosted', 'wrong-target', 'failed-work', 'replay', 'forged-host', 'forged-source', 'expired')) {
        $results = Join-Path $source ".results\$case"
        New-Item -ItemType Directory -Path $results | Out-Null
        $ready = Join-Path $fixture "$case-ready"
        $release = Join-Path $fixture "$case-release"
        $env:GITHUB_ACTIONS = if ($case -ceq 'local') { '' } else { 'true' }
        $env:RUNNER_ENVIRONMENT = if ($case -ceq 'self-hosted') { 'self-hosted' } else { 'github-hosted' }
        if ($case -ceq 'expired') { $env:SCALE_STARTED_AT = [DateTimeOffset]::UtcNow.AddSeconds(-650).ToString('O') }
        $probeFailure = $null
        try {
            if ($case -cin @('replay', 'forged-host', 'forged-source', 'expired')) {
                $signingRevision = if ($case -ceq 'forged-source') { $revision } else { $controllerRevision }
                $signerArguments = @('-NoProfile', '-File', "`"$signing`"", '-Proof', "`"$results\cleanup-proof.json`"",
                    '-Trust', "`"$trust`"", '-Factory', "`"$root\tests\CleanupProvenanceFixture.cs`"", '-Revision', $signingRevision)
                if ($case -ceq 'replay') { $signerArguments += @('-ReplayBundle', "`"$replay`"") }
                elseif ($case -ceq 'expired') { $signerArguments += @('-DelaySeconds', '10') }
                elseif ($case -ceq 'forged-host') { $signerArguments += @('-Hosting', 'self-hosted') }
                $signer = & $startProcess pwsh -ArgumentList $signerArguments -PassThru -NoNewWindow
            }
            try {
                Invoke-ScaleSuite -Suite non-ui-performance -Label "Strict $case" -ResultsDirectory $results `
                    -ExpectedRevision $revision -ExpectedPolicyVersion ci-timing-v4 -Selection $selection -Verify $verify
            } catch { $probeFailure = $_ }
            $actual = Get-Content (Join-Path $results 'result.json') -Raw | ConvertFrom-Json
            if (!$probeFailure -or $actual.outcome -ceq 'success' -or $actual.cleanup.warning) {
                throw "The public native runner accepted $case."
            }
            if ($case -ceq 'failed-work' -and !$actual.failure.StartsWith('Intentional required assertion failure.')) {
                throw 'Native cleanup erased the initiating verification failure.'
            }
            if ($case -ceq 'expired' -and $actual.outcome -cne 'timed_out') { throw 'Expired signing time escaped the original budget.' }
        } finally {
            $env:SCALE_STARTED_AT = $previousStarted
            if ($holder) {
                [IO.File]::WriteAllText($release, 'release')
                if (!$holder.WaitForExit(5000)) { throw "Controlled holder $($holder.Id) did not release." }
                $holder.Dispose(); $holder = $null
            }
            if ($signer) {
                if (!$signer.WaitForExit(5000)) { $signer.Kill($true); $signer.WaitForExit() }
                $signer.Dispose(); $signer = $null
            }
        }
    }
    Write-Host 'PASS: actual local/self-hosted/incorrect-project/failed-work/replay/forged-certificate-host-and-source/expired-budget native cases stay strict.'
    foreach ($legacy in @('rich-scale-original-v1', 'ci-timing-v2', 'ci-timing-v3')) {
        if ($legacy -ceq 'rich-scale-original-v1') { & git -C $source rm --quiet eng/timing-policy.json }
        else {
            @{ version = $legacy } | ConvertTo-Json | Set-Content -LiteralPath "$source\eng\timing-policy.json"
            & git -C $source add eng/timing-policy.json
        }
        & git -C $source -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m "Controlled legacy source $legacy"
        $legacyRevision = (& git -C $source rev-parse HEAD).Trim()
        $selection.cohortId = "$legacyRevision-321-1"
        $case = $legacy
        $results = Join-Path $source ".results\$legacy"
        $ready = Join-Path $fixture "$legacy-ready"
        $release = Join-Path $fixture "$legacy-release"
        $legacyFailure = $null
        try {
            try {
                Invoke-ScaleSuite -Suite non-ui-performance -Label "Original $legacy" -ResultsDirectory $results `
                    -ExpectedRevision $legacyRevision -ExpectedPolicyVersion $legacy -Selection $selection -Verify $verify
            } catch { $legacyFailure = $_ }
            $actual = Get-Content (Join-Path $results 'result.json') -Raw | ConvertFrom-Json
            if (!$legacyFailure -or $actual.outcome -cne 'failure' -or $actual.observedOutcome -cne 'success' -or
                !$actual.failure.Contains('e_sqlite3.dll') -or $actual.PSObject.Properties['acceptancePolicyVersion']) {
                throw 'A current signature/policy reclassified original-policy native cleanup failure.'
            }
        } finally {
            if ($holder) {
                [IO.File]::WriteAllText($release, 'release')
                if (!$holder.WaitForExit(5000)) { throw 'Controlled legacy holder did not release.' }
                $holder.Dispose(); $holder = $null
            }
        }
    }
    Write-Host 'PASS: actual v1/v2/v3 source-bound native refusals retain failed original verdicts.'
    '{"version":"ci-timing-v4"}' | Set-Content -LiteralPath "$source\eng\timing-policy.json"
    @'
param([string]$ExpectedRevision, [string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\PerformanceSuite.ps1"
. "$PSScriptRoot\ScaleRunner.ps1"
$selection = @{ schemaVersion = 1; filter = $PerformanceFilter; requiredTests = $PerformanceTests
    invocationId = [guid]::NewGuid().ToString('N'); runId = '321'; runAttempt = '1'; policyVersion = 'non-ui-original-v1' }
Invoke-ScaleSuite -Suite non-ui-performance -Label 'Supervised public fixture' -ExpectedRevision $ExpectedRevision `
    -ResultsDirectory $ResultsDirectory -Selection $selection -Verify {
        param($run, $build)
        Invoke-ScaleProcess restore pwsh @('-NoProfile', '-Command', 'exit 0')
        Invoke-ScaleProcess test pwsh @('-NoProfile', '-Command', 'exit 0')
        "<TestRun><Results><UnitTestResult testName=`"$($PerformanceTests[0])`" outcome=`"Passed`" /></Results><ResultSummary outcome=`"Completed`"><Counters total=`"1`" executed=`"1`" passed=`"1`" failed=`"0`" notExecuted=`"0`" /></ResultSummary></TestRun>" |
            Set-Content -LiteralPath (Join-Path $run 'result.trx')
        Assert-PerformanceResults $run
    }
throw 'Intentional failure after terminal result writing.'
'@ | Set-Content -LiteralPath "$source\eng\Run-Performance.ps1"
    & git -C $source add eng/Run-Performance.ps1 eng/timing-policy.json
    & git -C $source -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Controlled external process outcome'
    $supervisedRevision = (& git -C $source rev-parse HEAD).Trim()
    $supervisedResults = Join-Path $source '.results\supervised-failure'
    $supervisedFailure = $null
    try {
        & "$source\eng\Invoke-ScaleWorker.ps1" -Stage Start -Worker non-ui-performance `
            -ExpectedRevision $supervisedRevision -ResultsDirectory $supervisedResults
    } catch { $supervisedFailure = $_ }
    $receipt = Get-Content (Join-Path $supervisedResults 'worker-exit.json') -Raw | ConvertFrom-Json
    $terminal = Get-Content (Join-Path $supervisedResults 'result.json') -Raw | ConvertFrom-Json
    if (!$supervisedFailure -or $receipt.exitCode -ne 1 -or $terminal.outcome -cne 'success') {
        throw 'Saved success erased the real supervised process failure.'
    }
    Write-Host 'PASS: the public supervisor refuses a real failed process even after terminal success was saved.'
} finally {
    if ($previousHosting) { $env:GITHUB_ACTIONS, $env:RUNNER_ENVIRONMENT, $env:RUNNER_OS = $previousHosting }
    if ($holder) {
        [IO.File]::WriteAllText((Join-Path $fixture 'release-native'), 'release')
        if (!$holder.WaitForExit(5000)) { throw "Test-owned native holder $($holder.Id) did not release." }
        $holder.Dispose()
    }
    if ($signer) {
        if (!$signer.WaitForExit(5000)) { $signer.Kill($true); $signer.WaitForExit() }
        $signer.Dispose()
    }
    Set-Location $root
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
