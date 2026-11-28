# Synthetic hosted inputs with real signature verification; not hosted workload measurements.
function Add-SignedCleanupFixture([string]$Directory, [string]$ControllerRevision, [hashtable]$Roots) {
    if (!('CleanupProvenanceFixture' -as [type])) {
        Add-Type -Path "$PSScriptRoot\CleanupProvenanceFixture.cs" -ReferencedAssemblies `
            System.Security.Cryptography.Pkcs,System.Security.Cryptography,System.Formats.Asn1,System.Text.Json,System.Runtime,System.Runtime.Numerics,System.Collections
    }
    $path = Join-Path $Directory 'result.json'
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable -Depth 40 -DateKind String
    $run = Join-Path $Directory $report.runDirectory
    $project = if ($report.suite -ceq 'non-ui-performance') { 'OrgExplorer.Persistence.Tests' } else { 'OrgExplorer.Desktop' }
    $relative = "bin\$project\release\runtimes\win-x64\native\e_sqlite3.dll"
    $build = "C:\synthetic\$($report.runDirectory)\build"
    $report.acceptancePolicyVersion = 'ci-timing-v4'
    $report.failure = $null
    $report.verificationOutcome = 'success'
    $report.acceptanceDeadlineAt = ([DateTimeOffset]$report.startedAt).AddSeconds($report.ceilingSeconds).ToString('O')
    $report.cleanup = @{
        outcome = 'failure'; warning = $true; phase = 'build-housekeeping'
        path = Join-Path $build $relative
        error = 'Controlled compiled native cleanup refusal; holder unknown.'
        errorType = 'System.UnauthorizedAccessException'; hresult = -2147024891
        observedAt = ([DateTimeOffset]$report.completedAt).AddSeconds(-1).ToString('O')
        native = @{ path = Join-Path $build $relative; relativePath = $relative; bytes = 1; sha256 = 'D' * 64 }
        remainingBuild = @{ root = $build; exists = $true; files = @(@{ path = $relative; bytes = 1 }) }
    }
    $report.cleanup | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $run 'cleanup-failure.json')
    $proof = $report | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable -Depth 40 -DateKind String
    $proof.schemaVersion = 1; $proof.repository = 'geevensingh/org-explorer'
    $proof.nonce = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
    if ($report.suite -ceq 'non-ui-performance') {
        $profileLine = @(Get-Content (Join-Path $run 'scale-measurements.log') |
            Where-Object { $_.StartsWith('SCALE ORDINARY-PROFILE ') })[0]
        $proof.measurements = @{ profile = $profileLine.Substring('SCALE ORDINARY-PROFILE '.Length) | ConvertFrom-Json -AsHashtable; phasesMs = @{} }
        foreach ($phase in $NonUiCohortPhases) { $proof.measurements.phasesMs[$phase] = @{ value = 10.0; outcome = 'Passed' } }
        $proof.tests = @{ total = 1; executed = 1; passed = 1; failed = 0; notExecuted = 0; requiredNames = @($PerformanceTests) }
    } else {
        $proof.phasesMs = @{}
        $occurrences = @{}
        foreach ($line in Get-Content (Join-Path $run 'ui-scale.log')) {
            if ($line -match '^SCALE END (?<name>[^;]+);') {
                $name = $Matches.name
                if (!$occurrences.ContainsKey($name)) { $occurrences[$name] = 0 }
                $occurrences[$name]++
                $key = if ($occurrences[$name] -eq 1) { $name } else { "$name#$($occurrences[$name])" }
                $proof.phasesMs[$key] = @{ value = 0.0; outcome = 'Passed' }
            }

        }
        $proof.evidence = @{}
        if ($report.worker -ceq 'prefix') {
            foreach ($phase in @('normal-executable', 'background-export')) {
                $proof.evidence["note-$phase"] = Get-Content (Join-Path $run "scale-mutation-save-prefix-ui-sqlite-note-latency-rich-$phase.json") -Raw |
                    ConvertFrom-Json -AsHashtable -DateKind String
            }
            $proof.evidence.lifecycle = Get-Content (Join-Path $run 'scale-mutation-save-prefix-ui-sqlite-rich-export-lifecycle.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
            $proof.handoff = Get-Content (Join-Path $run 'handoff\handoff.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String
        } else { $proof.handoff = Get-Content (Join-Path $run 'handoff-received.json') -Raw | ConvertFrom-Json -AsHashtable -DateKind String }
        $phase = if ($report.worker -ceq 'prefix') { 'background-export' } else { 'normal-executable' }
        $scenario = if ($report.worker -ceq 'prefix') { 'scale-mutation-save-prefix' } else { 'scale-mutation-delivery-tail' }
        $proof.evidence["profile-$phase"] = Get-Content (Join-Path $run "$scenario-ui-sqlite-rich-profile-latency-$phase.json") -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    }
    $proof.files = @(Get-ChildItem -LiteralPath $run -File -Recurse | Where-Object {
        $_.Name -cnotin @('result.json', 'cleanup-proof.json', 'cleanup-attestation.json')
    } | ForEach-Object {
        @{ path = [IO.Path]::GetRelativePath($run, $_.FullName); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    } | Sort-Object path)
    $proofText = $proof | ConvertTo-Json -Depth 40
    $subject = Join-Path $run 'cleanup-proof.json'
    $bundle = Join-Path $run 'cleanup-attestation.json'
    $trust = Join-Path $Directory 'fixture-trust-root.json'
    [IO.File]::WriteAllText($subject, $proofText)
    [CleanupProvenanceFixture]::Sign($subject, $bundle, $trust, $ControllerRevision, $report.runId, $report.runAttempt,
        'github-hosted', ([DateTimeOffset]$report.completedAt).AddTicks(-1), 'rich-cohort-sample.yml')
    $Roots[(Get-FileHash -LiteralPath $subject).Hash] = $trust
    $report.cleanup.proof = $proofText
    $report.cleanup.attestation = Get-Content -LiteralPath $bundle -Raw
    $text = $report | ConvertTo-Json -Depth 40
    [IO.File]::WriteAllText($path, $text)
    [IO.File]::WriteAllText((Join-Path $run 'result.json'), $text)
}

function Invoke-CleanupFixtureApi([string]$Endpoint, [string]$SourceRemote, [hashtable]$Contexts) {
    switch -Regex ($Endpoint) {
        '/git/trees/(?<revision>[0-9a-f]{40})\?recursive=1$' {
            $entry = (& git "--git-dir=$SourceRemote" ls-tree $Matches.revision -- eng/timing-policy.json) -split '\s+'
            if ($LASTEXITCODE -ne 0 -or $entry.Count -lt 4) { throw 'Fixture source tree unavailable.' }
            return @{ truncated = $false; tree = @(@{ path = 'eng/timing-policy.json'; type = 'blob'; sha = $entry[2] }) }
        }
        '/git/blobs/(?<blob>[0-9a-f]{40})$' {
            $text = (& git "--git-dir=$SourceRemote" cat-file blob $Matches.blob) -join "`n"
            if ($LASTEXITCODE -ne 0) { throw 'Fixture source blob unavailable.' }
            return @{ encoding = 'base64'; content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text)) }
        }
        '/actions/runs/(?<run>[0-9]+)/attempts/(?<attempt>[0-9]+)$' {
            $key = "$($Matches.run)-$($Matches.attempt)"
            if (!$Contexts.ContainsKey($key)) { throw 'Fixture run context unavailable.' }
            return $Contexts[$key]
        }
        default { throw "Unexpected fixture GitHub endpoint: $Endpoint" }
    }
}

function Start-CleanupFixtureProcess([string]$FilePath, [object[]]$ArgumentList, [hashtable]$Roots,
    [switch]$PassThru, [switch]$NoNewWindow, [string]$RedirectStandardOutput, [string]$RedirectStandardError) {
    if ($FilePath -ceq 'gh') {
        $subject = [string]$ArgumentList[2]
        $hash = (Get-FileHash -LiteralPath $subject.Trim('"')).Hash
        if (!$Roots.ContainsKey($hash)) { throw 'No controlled external trust root exists for this proof.' }
        $ArgumentList += @('--custom-trusted-root', ('"' + $Roots[$hash] + '"'))
        $FilePath = @(Get-Command gh -CommandType Application)[0].Source
    }
    $parameters = @{ FilePath = $FilePath; ArgumentList = $ArgumentList; PassThru = $PassThru; NoNewWindow = $NoNewWindow }
    if ($RedirectStandardOutput) { $parameters.RedirectStandardOutput = $RedirectStandardOutput }
    if ($RedirectStandardError) { $parameters.RedirectStandardError = $RedirectStandardError }
    & (Get-Command Start-Process -CommandType Cmdlet) @parameters
}
