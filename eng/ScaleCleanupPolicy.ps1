. "$PSScriptRoot\FilesystemHealth.ps1"
. "$PSScriptRoot\TimingPolicy.ps1"

function Get-ScaleBuildInventory([string]$Build) {
    $files = @()
    if (Test-Path -LiteralPath $Build) {
        foreach ($file in Get-ChildItem -LiteralPath $Build -File -Recurse -Force) {
            $files += @{ path = [IO.Path]::GetRelativePath($Build, $file.FullName); bytes = $file.Length }
        }
    }
    return @{ root = $Build; exists = [bool](Test-Path -LiteralPath $Build); files = $files }
}

function Test-ScaleNativeBuildPath([string]$RelativePath, [string]$Suite) {
    $projects = switch ($Suite) {
        'non-ui-performance' { @('OrgExplorer.Persistence.Tests') }
        'ui-scale' { @('OrgExplorer.Desktop', 'OrgExplorer.Desktop.Smoke') }
        default { @() }
    }
    $parts = @($RelativePath -split '\\')
    return $parts.Count -eq 7 -and $parts[0] -ceq 'bin' -and $projects -ccontains $parts[1] -and
        $parts[2] -ceq 'release' -and $parts[3] -ceq 'runtimes' -and $parts[4] -ceq 'win-x64' -and
        $parts[5] -ceq 'native' -and $parts[6] -ceq 'e_sqlite3.dll'
}

function Get-ScaleNativeCleanupTarget([Management.Automation.ErrorRecord]$Failure, [string]$Build, [string]$Suite) {
    if ($Failure.Exception -isnot [UnauthorizedAccessException] -or $Failure.Exception.HResult -ne -2147024891 -or
        $Failure.FullyQualifiedErrorId -cnotmatch ',Microsoft\.PowerShell\.Commands\.RemoveItemCommand$') { return $null }
    $path = if ($Failure.TargetObject -is [IO.FileSystemInfo]) { $Failure.TargetObject.FullName }
        else { [string]$Failure.TargetObject }
    if (!$path -or ![IO.Path]::IsPathFullyQualified($path)) { return $null }
    $path = [IO.Path]::GetFullPath($path)
    $relative = [IO.Path]::GetRelativePath($Build, $path)
    if (!(Test-ScaleNativeBuildPath $relative $Suite)) { return $null }
    $item = Get-Item -LiteralPath $path
    for ($current = $item; $current -and $current.FullName -cne (Split-Path $Build -Parent);
        $current = if ($current -is [IO.DirectoryInfo]) { $current.Parent } else { $current.Directory }) {
        if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $null }
    }
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 256 -or $bytes[0] -ne 77 -or $bytes[1] -ne 90) { return $null }
    $offset = [BitConverter]::ToInt32($bytes, 60)
    if ($offset -lt 64 -or $offset -gt $bytes.Length - 26 -or
        [BitConverter]::ToUInt32($bytes, $offset) -ne 17744 -or
        [BitConverter]::ToUInt16($bytes, $offset + 4) -ne 34404 -or
        !([BitConverter]::ToUInt16($bytes, $offset + 22) -band 8192) -or
        [BitConverter]::ToUInt16($bytes, $offset + 24) -ne 523) { return $null }
    return @{ path = $path; relativePath = $relative; bytes = $bytes.Length
        sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) }
}

function Get-ScaleProofFiles([string]$Run) {
    $files = @()
    foreach ($file in Get-ChildItem -LiteralPath $Run -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($Run, $file.FullName)
        if ($relative.StartsWith('build\', [StringComparison]::OrdinalIgnoreCase) -or
            $relative -cin @('result.json', 'cleanup-proof.json', 'cleanup-attestation.json') -or
            $file.Extension -cnotin @('.json', '.log', '.trx', '.oesnapshot', '.png')) { continue }
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Cleanup proof cannot follow linked result files.' }
        $files += @{ path = $relative; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName).Hash }
    }
    return @($files | Sort-Object path)
}

function Copy-ScaleEvidenceDirectory([string]$Source, [string]$Destination) {
    $report = Get-Content -LiteralPath (Join-Path $Source 'result.json') -Raw | ConvertFrom-Json
    if ($report.runDirectory -cnotmatch '^run-[0-9a-f]{32}$' -or (Test-Path -LiteralPath $Destination)) {
        throw 'Evidence copy requires a bound source and a new owned destination.'
    }
    New-Item -ItemType Directory -Path $Destination | Out-Null
    $buildPrefix = $report.runDirectory + '\build\'
    foreach ($file in Get-ChildItem -LiteralPath $Source -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        if ($relative.StartsWith($buildPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            $file.Extension.ToLowerInvariant() -cnotin @('.json', '.log', '.trx', '.oesnapshot', '.png')) { continue }
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Evidence selection cannot follow linked files.' }
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

function Read-ScaleCleanupRunContext([string]$RunId, [string]$RunAttempt) {
    $text = & gh api "repos/geevensingh/org-explorer/actions/runs/$RunId/attempts/$RunAttempt"
    if ($LASTEXITCODE -ne 0) { throw 'Authenticated cleanup run context is unavailable.' }
    $run = ($text -join "`n") | ConvertFrom-Json -AsHashtable -DateKind String
    if ($run.id -ne [long]$RunId -or $run.run_attempt -ne [int]$RunAttempt -or
        $run.head_repository.full_name -cne 'geevensingh/org-explorer' -or
        $run.head_sha -cnotmatch '^[0-9a-f]{40}$') { throw 'Cleanup signing run context differs from this execution.' }
    return $run
}

function Assert-ScaleCleanupDisposition($Report, [string]$ExpectedPolicyVersion, [string]$Directory,
    [switch]$DuringExecution) {
    $report = $Report | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable -Depth 40 -DateKind String
    if ($report.ContainsKey('acceptancePolicyVersion') -and $report.acceptancePolicyVersion -cne $ExpectedPolicyVersion) {
        throw 'Cleanup acceptance policy differs from the independently expected source.'
    }
    if ($report.ContainsKey('verificationMode')) {
        throw 'Worker cleanup validation cannot use a collection verification mode.'
    }
    if ($ExpectedPolicyVersion -ceq 'ci-timing-v4' -and $report.suite -cin @('non-ui-performance', 'ui-scale') -and
        ($report.acceptancePolicyVersion -cne 'ci-timing-v4' -or !$report.ContainsKey('cleanup') -or
            $report.cleanup.warning -isnot [bool] -or
            ($report.outcome -ceq 'success' -and ($report.verificationOutcome -cne 'success' -or
                ($report.cleanup.outcome -cne 'success' -and !$report.cleanup.warning))))) {
        throw 'Current source requires explicit verification and honest housekeeping results.'
    }
    if (!$report.ContainsKey('cleanup') -or (!$report.cleanup.warning -and !$DuringExecution)) { return }
    foreach ($field in @('ceilingSeconds', 'observationSeconds', 'executionSeconds')) { Assert-HealthNumber $report[$field] $field }
    $policy = Get-ApprovedTimingPolicy $ExpectedPolicyVersion
    $ceiling = if ($report.suite -ceq 'ui-scale' -and $report.worker -ceq 'prefix') { $policy.prefixSafetySeconds } else { $policy.tailSafetySeconds }
    $sourceVersion = Get-SourceTimingPolicyVersion $report.revision -Hosted
    if ($ExpectedPolicyVersion -cne 'ci-timing-v4' -or
        $sourceVersion -cne 'ci-timing-v4' -or
        $report.acceptancePolicyVersion -cne 'ci-timing-v4' -or
        $report.verificationOutcome -cne 'success' -or $report.observedOutcome -cne 'success' -or
        $report.failure -or $report.trackedChanges -or $report.untrackedChanges -or $report.diagnosticOnly -or
        $report.cleanup.outcome -cne 'failure' -or $report.cleanup.phase -cne 'build-housekeeping' -or
        $report.cleanup.errorType -cne 'System.UnauthorizedAccessException' -or $report.cleanup.hresult -ne -2147024891 -or
        $report.suite -cnotin @('non-ui-performance', 'ui-scale') -or
        ($report.suite -ceq 'ui-scale' -and $report.worker -cnotin @('prefix', 'tail')) -or
        $report.trackedChanges -isnot [bool] -or $report.untrackedChanges -isnot [bool] -or
        $report.diagnosticOnly -isnot [bool] -or
        $report.ceilingSeconds -ne $ceiling -or $report.observationSeconds -ne $ceiling -or
        ![double]::IsFinite($report.executionSeconds) -or $report.executionSeconds -lt 0 -or
        $report.executionSeconds -gt $ceiling -or
        [DateTimeOffset]$report.acceptanceDeadlineAt -ne ([DateTimeOffset]$report.startedAt).AddSeconds($ceiling)) {
        throw "Cleanup warning is not authorized: expected/source/declared=$ExpectedPolicyVersion/$sourceVersion/$($report.acceptancePolicyVersion); suite/worker=$($report.suite)/$($report.worker); verification/observed=$($report.verificationOutcome)/$($report.observedOutcome); budget/observation/expected=$($report.ceilingSeconds)/$($report.observationSeconds)/$ceiling; started/deadline=$($report.startedAt)/$($report.acceptanceDeadlineAt)."
    }
    $proofText = $report.cleanup.proof
    $bundleText = $report.cleanup.attestation
    if ($proofText -isnot [string] -or $bundleText -isnot [string] -or !$proofText -or !$bundleText) {
        throw 'Cleanup warning lacks its signed original proof.'
    }
    $proof = $proofText | ConvertFrom-Json -AsHashtable -Depth 40 -DateKind String
    foreach ($field in @('revision', 'runId', 'runAttempt', 'runDirectory', 'suite', 'worker', 'cohortId', 'sample',
        'startedAt', 'acceptanceDeadlineAt', 'ceilingSeconds', 'acceptancePolicyVersion')) {
        $actual = if ($report.ContainsKey($field)) { $report[$field] } else { $null }
        $different = if ($field -cin @('startedAt', 'acceptanceDeadlineAt')) {
            [DateTimeOffset]$proof[$field] -ne [DateTimeOffset]$actual
        } else { ($proof[$field] | ConvertTo-Json -Compress) -cne ($actual | ConvertTo-Json -Compress) }
        if ($different) {
            throw "Signed cleanup proof differs from expected execution: $field"
        }
    }
    if ($proof.schemaVersion -ne 1 -or $proof.repository -cne 'geevensingh/org-explorer' -or
        $proof.verificationOutcome -cne 'success' -or $proof.nonce -cnotmatch '^[0-9a-f]{64}$' -or
        $proof.cleanup.path -cne $report.cleanup.path -or
        $proof.cleanup.error -cne $report.cleanup.error -or
        $proof.cleanup.phase -cne $report.cleanup.phase -or
        $proof.cleanup.errorType -cne $report.cleanup.errorType -or $proof.cleanup.hresult -ne $report.cleanup.hresult -or
        $proof.cleanup.native.sha256 -cnotmatch '^[0-9A-F]{64}$' -or
        !@($proof.files).Count -or @($proof.steps).Count -ne @($report.steps).Count) {
        throw 'Cleanup proof is malformed or no longer describes the original refusal/workload.'
    }
    for ($index = 0; $index -lt $proof.steps.Count; $index++) {
        foreach ($field in @('name', 'seconds', 'exitCode')) {
            if ($proof.steps[$index][$field] -ne $report.steps[$index][$field] -or
                $report.steps[$index].exitCode -ne 0) { throw 'Signed workload command evidence differs or failed.' }
        }
    }
    $names = if ($report.suite -ceq 'non-ui-performance') { @('restore', 'test') }
        else { @('restore-app', 'restore-tests', 'build-app', 'display-prerequisite', 'ui-scale') }
    if (($report.steps.name -join "`n") -cne ($names -join "`n")) { throw 'Signed cleanup cannot substitute another workload.' }
    if (!(Test-ScaleNativeBuildPath $proof.cleanup.native.relativePath $report.suite) -or
        $proof.cleanup.remainingBuild.root -cnotlike "*\$($report.runDirectory)\build" -or
        $proof.cleanup.native.path -cne $proof.cleanup.path -or
        [IO.Path]::GetFullPath((Join-Path $proof.cleanup.remainingBuild.root $proof.cleanup.native.relativePath)) -cne $report.cleanup.path) {
        throw 'Cleanup proof is outside the allocated compiled native build scope.'
    }
    $remaining = @($proof.cleanup.remainingBuild.files | Sort-Object path)
    $actualRemaining = @($report.cleanup.remainingBuild.files | Sort-Object path)
    if ($remaining.Count -ne $actualRemaining.Count -or
        $proof.cleanup.remainingBuild.root -cne $report.cleanup.remainingBuild.root) { throw 'Remaining owned build state changed in the report.' }
    for ($index = 0; $index -lt $remaining.Count; $index++) {
        if ($remaining[$index].path -cne $actualRemaining[$index].path -or
            $remaining[$index].bytes -ne $actualRemaining[$index].bytes) { throw 'Remaining build metadata differs from signed refusal.' }
    }
    if ($Directory) {
        $runDirectory = Join-Path $Directory $report.runDirectory
        $files = @(Get-ScaleProofFiles $runDirectory)
        $expectedFiles = @($proof.files | Sort-Object path)
        if ((Get-Content -LiteralPath (Join-Path $runDirectory 'cleanup-proof.json') -Raw) -cne $proofText -or
            (Get-Content -LiteralPath (Join-Path $runDirectory 'cleanup-attestation.json') -Raw) -cne $bundleText -or
            $files.Count -ne $expectedFiles.Count) {
            throw 'Signed cleanup evidence inventory is missing or changed.'
        }
        for ($index = 0; $index -lt $files.Count; $index++) {
            foreach ($field in @('path', 'bytes', 'sha256')) {
                if ($files[$index][$field] -cne $expectedFiles[$index][$field]) { throw 'Signed raw cleanup/workload evidence changed.' }
            }
        }
    }
    $witnessEnd = if ($DuringExecution) { [DateTimeOffset]$proof.acceptanceDeadlineAt }
        else { [DateTimeOffset]$report.completedAt }
    $verificationEnd = if ($DuringExecution) { [DateTimeOffset]$proof.acceptanceDeadlineAt }
        else { [DateTimeOffset]::UtcNow.AddSeconds(120) }
    $null = Read-VerifiedScaleSignature -ProofText $proofText -BundleText $bundleText `
        -Revision $report.revision -RunId $report.runId -RunAttempt $report.runAttempt -ExpectedNonce $proof.nonce `
        -WitnessNotBefore ([DateTimeOffset]$proof.cleanup.observedAt) -WitnessNotAfter $witnessEnd `
        -VerificationDeadline $verificationEnd
}

function Complete-ScalePreparedCleanup([hashtable]$Report, [string]$Directory) {
    if ($Report.phase -cne 'cleanup-authentication' -or $Report.outcome -cne 'running' -or
        $Report.cleanup.warning -or !$Report.cleanup.authenticationStartedAt) {
        throw 'Cleanup completion requires the original unaccepted prepared record.'
    }
    $deadline = [DateTimeOffset]$Report.acceptanceDeadlineAt
    try {
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Prepared cleanup exhausted its original execution budget.' }
        $Report.cleanup.attestation = Get-Content -LiteralPath (Join-Path $Directory "$($Report.runDirectory)\cleanup-attestation.json") -Raw
        Assert-ScaleCleanupDisposition $Report ci-timing-v4 $Directory -DuringExecution
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Cleanup verification exhausted its original execution budget.' }
        $Report.cleanup.warning = $true
        $Report.cleanup.authenticationSeconds =
            ([DateTimeOffset]::UtcNow - [DateTimeOffset]$Report.cleanup.authenticationStartedAt).TotalSeconds
        $Report.outcome = 'success'
        Write-Warning "Cleanup FAILED at $($Report.cleanup.path): $($Report.cleanup.error). Signed disposable-worker housekeeping warning; holder unknown."
    } catch {
        $Report.cleanup.authenticationFailure = $_.Exception.Message
        $Report.outcome = if ([DateTimeOffset]::UtcNow -ge $deadline) { 'timed_out' } else { 'failure' }
        $Report.failure = $_.Exception.Message
        throw
    } finally {
        $Report.phase = 'finished'
        $Report.completedAt = [DateTimeOffset]::UtcNow.ToString('O')
        $Report.executionSeconds = ([DateTimeOffset]::UtcNow - [DateTimeOffset]$Report.startedAt).TotalSeconds
        if ([DateTimeOffset]$Report.completedAt -ge $deadline -and $Report.outcome -ceq 'success') {
            $Report.outcome = 'timed_out'
            $Report.cleanup.warning = $false
            $Report.failure = 'Cleanup completion exhausted its original execution budget.'
        }
        $json = $Report | ConvertTo-Json -Depth 12
        foreach ($path in @((Join-Path $Directory 'result.json'), (Join-Path $Directory "$($Report.runDirectory)\result.json"))) {
            [IO.File]::WriteAllText("$path.writing", $json)
            [IO.File]::Move("$path.writing", $path, $true)
        }
        if ($Report.outcome -cne 'success') { throw $Report.failure }
    }
}

function Read-VerifiedScaleSignature(
    [string]$ProofText, [string]$BundleText,
    [ValidatePattern('^[0-9a-f]{40}$')][string]$Revision,
    [ValidatePattern('^[1-9][0-9]*$')][string]$RunId,
    [ValidatePattern('^[1-9][0-9]*$')][string]$RunAttempt,
    [ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedNonce,
    [DateTimeOffset]$WitnessNotBefore, [DateTimeOffset]$WitnessNotAfter,
    [DateTimeOffset]$VerificationDeadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
) {
    $proof = $ProofText | ConvertFrom-Json -AsHashtable -Depth 40 -DateKind String
    if ($proof.repository -cne 'geevensingh/org-explorer' -or $proof.revision -cne $Revision -or
        $proof.runId -cne $RunId -or $proof.runAttempt -cne $RunAttempt -or $proof.nonce -cne $ExpectedNonce -or
        $WitnessNotAfter -lt $WitnessNotBefore -or
        (Get-SourceTimingPolicyVersion $Revision -Hosted) -cne 'ci-timing-v4') {
        throw 'Signed proof differs from independent expected source/run/nonce context.'
    }
    $context = Read-ScaleCleanupRunContext $RunId $RunAttempt
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('org-explorer-cleanup-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $process = $null
    try {
        $subject = Join-Path $temporary 'proof.json'
        $bundle = Join-Path $temporary 'bundle.json'
        [IO.File]::WriteAllText($subject, $proofText)
        [IO.File]::WriteAllText($bundle, $bundleText)
        $arguments = @('attestation', 'verify', $subject, '--bundle', $bundle,
            '--repo', 'geevensingh/org-explorer', '--deny-self-hosted-runners',
            '--cert-oidc-issuer', 'https://token.actions.githubusercontent.com',
            '--source-digest', $context.head_sha, '--cert-identity-regex',
            '^https://github\.com/geevensingh/org-explorer/\.github/workflows/(scale-verification|rich-cohort-sample|non-ui-cohort-execution)\.yml@',
            '--format', 'json')
        $seconds = ($VerificationDeadline - [DateTimeOffset]::UtcNow).TotalSeconds
        if ($seconds -le 0) { throw 'Cleanup authentication exhausted the original execution budget.' }
        $output = Join-Path $temporary 'verified.json'
        $errors = Join-Path $temporary 'verification-errors.log'
        $process = Start-Process gh -ArgumentList @($arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) `
            -PassThru -NoNewWindow -RedirectStandardOutput $output -RedirectStandardError $errors
        if (!$process.WaitForExit([int][Math]::Min([int]::MaxValue, $seconds * 1000))) {
            throw 'Cleanup signature verification did not complete within its budget.'
        }
        if ($process.ExitCode -ne 0) { throw "Cleanup signature rejected: $(Get-Content -LiteralPath $errors -Raw)" }
        $verified = @(Get-Content -LiteralPath $output -Raw | ConvertFrom-Json -AsHashtable -Depth 40 -DateKind String)
        if ($verified.Count -ne 1) { throw 'Cleanup requires exactly one verified original attestation.' }
        $certificate = $verified[0].verificationResult.signature.certificate
        $signer = $certificate.buildSignerURI
        $expectedSignerSha = $null
        if ($signer -cmatch ('^https://github\.com/geevensingh/org-explorer/' + [regex]::Escape($context.path) + '@')) {
            $expectedSignerSha = $context.head_sha
        }
        $references = if ($context.ContainsKey('referenced_workflows')) { @($context.referenced_workflows) } else { @() }
        foreach ($reference in $references) {
            $identities = @("https://github.com/$($reference.path)")
            if ($reference.ContainsKey('ref') -and $reference.ref) {
                $workflowPath = $reference.path -replace '@[^@]+$', ''
                $identities += "https://github.com/$workflowPath@$($reference.ref)"
            }
            if ($identities -ccontains $signer) { $expectedSignerSha = $reference.sha }
        }
        if ($certificate.runnerEnvironment -cne 'github-hosted' -or
            $certificate.sourceRepositoryIdentifier -cne '1448772658' -or
            $certificate.sourceRepositoryOwnerIdentifier -cne '11344767' -or
            $certificate.sourceRepositoryDigest -cne $context.head_sha -or
            $certificate.runInvocationURI -cne "https://github.com/geevensingh/org-explorer/actions/runs/$RunId/attempts/$RunAttempt" -or
            $expectedSignerSha -cnotmatch '^[0-9a-f]{40}$' -or $certificate.buildSignerDigest -cne $expectedSignerSha -or
            (Get-SourceTimingPolicyVersion $expectedSignerSha -Hosted) -cne 'ci-timing-v4') {
            throw 'Verified signing certificate is not the expected disposable worker/source/run context.'
        }
        $timestamps = @($verified[0].verificationResult.verifiedTimestamps)
        if (!$timestamps.Count -or !@($timestamps | Where-Object {
            [DateTimeOffset]$_.timestamp -ge $WitnessNotBefore -and
            [DateTimeOffset]$_.timestamp -le $WitnessNotAfter
        }).Count) { throw 'Cleanup signature was not witnessed within the unchanged original budget.' }
        if ([DateTimeOffset]::UtcNow -ge $VerificationDeadline) {
            throw 'Cleanup signature verification exhausted the original execution budget.'
        }
        return $verified[0].verificationResult
    } finally {
        if ($process) {
            if (!$process.HasExited) {
                $process.Kill($true)
                $process.WaitForExit()
            }
            $process.Dispose()
        }
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
