function Set-TimingMainContext($History) {
    $null = Invoke-HealthGit $History.directory @('-c', 'credential.helper=',
        '-c', 'credential.helper=!gh auth git-credential', 'fetch', '--quiet', 'origin',
        'refs/heads/main:refs/heads/timing-main')
    if ((Invoke-HealthGit $History.directory @('rev-parse', '--is-shallow-repository')) -cne 'false') {
        throw 'Canonical timing references require complete actual main ancestry, never shallow or guessed order.'
    }
    $History.mainRevision = Invoke-HealthGit $History.directory @('rev-parse', 'refs/heads/timing-main')
    $History.mainOrder = @{}
    $revisions = (Invoke-HealthGit $History.directory @('rev-list', '--first-parent', '--reverse', $History.mainRevision)) -split "`n"
    for ($index = 0; $index -lt $revisions.Count; $index++) {
        $History.mainOrder[$revisions[$index]] = $index
    }
}

function Assert-TimingMainRevision($History, [string]$Revision) {
    if (!$History.mainOrder.ContainsKey($Revision)) {
        throw 'Timing candidate is not a verified actual main first-parent revision; source/order is unavailable.'
    }
}

    function Get-TimingControlPath($Record) {
        if ($Record.recordType -ceq 'timing-bootstrap') { return "timing-bootstrap/$($Record.policyVersion).json" }
        return "timing-plans/$($Record.revision)/$($Record.cohortRunId)/$($Record.cohortRunAttempt).json"
    }

    function Assert-TimingControl($Record, [string]$Path) {
        if ($Record.recordType -cnotin @('timing-bootstrap', 'timing-plan') -or
            $Record.repository -cne $FilesystemHealthRepository -or $Record.authority -cne 'trusted-main-push' -or
            $Record.policyVersion -cne 'ci-timing-v2' -or $Record.revision -cnotmatch '^[0-9a-f]{40}$' -or
            $Record.mainRevision -cnotmatch '^[0-9a-f]{40}$' -or $Path -cne (Get-TimingControlPath $Record)) {
            throw 'Malformed trusted timing bootstrap/plan provenance.'
        }
        Assert-HealthDate $Record.observedAt 'timing control observedAt'
        if ($Record.recordType -ceq 'timing-plan' -and ($Record.mode -cne 'automatic-baseline' -or
            $Record.bootstrapRevision -cnotmatch '^[0-9a-f]{40}$' -or
            $Record.cohortRunId -cnotmatch '^[1-9][0-9]*$' -or $Record.cohortRunAttempt -cnotmatch '^[1-9][0-9]*$' -or
            $Record.cohortId -cne "$($Record.revision)-$($Record.cohortRunId)-$($Record.cohortRunAttempt)" -or
            $Record.ciRevision -cne $Record.revision -or $Record.ciRunId -cnotmatch '^[1-9][0-9]*$' -or
            $Record.ciRunAttempt -cnotmatch '^[1-9][0-9]*$' -or $Record.collect -isnot [bool] -or
            ($Record.samples -join ',') -cne '1,2,3')) { throw 'Malformed automatic timing collection plan.' }
    }

    function Get-TimingReferenceRecordTypes {
        return @{
            'timing-bootstrap' = @{
                PathPattern = '^timing-bootstrap/ci-timing-v2\.json$'
                GetPath = { param($record) Get-TimingControlPath $record }
                Validate = { param($record, $path) Assert-TimingControl $record $path }
            }
            'timing-plan' = @{
                PathPattern = '^timing-plans/[0-9a-f]{40}/[1-9][0-9]*/[1-9][0-9]*\.json$'
                GetPath = { param($record) Get-TimingControlPath $record }
                Validate = { param($record, $path) Assert-TimingControl $record $path }
            }
        }
    }

    function Get-TimingBootstrap($History, [string]$ExplicitRevision) {
        $saved = @($History.records | Where-Object { $_.Contains('recordType') -and $_.recordType -ceq 'timing-bootstrap' })
        if ($saved.Count -gt 1) { throw 'Timing bootstrap is not uniquely pinned.' }
        if ($saved.Count) {
            if ($ExplicitRevision -and $ExplicitRevision -cne $saved[0].revision) {
                throw 'Explicit bootstrap contradicts the already durable source; it cannot move with main.'
            }
            Assert-TimingMainRevision $History $saved[0].revision
            return $saved[0].revision
        }
        if (!$ExplicitRevision) { throw 'Absent timing bootstrap requires an explicit trusted source, not an empty-history fallback.' }
        Assert-TimingMainRevision $History $ExplicitRevision
        if ($ExplicitRevision -cne $History.mainRevision) {
            throw 'The initial explicit bootstrap source must be current verified main when first planned.'
        }
        return $ExplicitRevision
    }

    function Assert-TimingAutomation($Collection, [long]$RunId, [int]$RunAttempt, [switch]$AllowLocalFixture) {
        if ($Collection.id -ne $RunId -or $Collection.run_attempt -ne $RunAttempt -or
            $Collection.head_repository.full_name -cne $FilesystemHealthRepository -or
            $Collection.event -cne 'workflow_run' -or $Collection.path -cne '.github/workflows/non-ui-cohorts.yml') {
            throw 'Only authenticated automatic collection can maintain timing references.'
        }
        if (!$AllowLocalFixture) {
            $source = Split-Path $PSScriptRoot -Parent
            $head = & git -C $source rev-parse HEAD
            if ($LASTEXITCODE -ne 0 -or $head -cne $env:GITHUB_WORKFLOW_SHA -or
                $head -cne $Collection.head_sha -or $env:GITHUB_ACTIONS -cne 'true' -or
                $env:GITHUB_REPOSITORY -cne $FilesystemHealthRepository) {
                throw 'Timing publication requires the committed authenticated hosted controller, not candidate artifact authority.'
            }
            $dirty = & git -C $source status --porcelain
            if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Timing controller source must be clean and committed.' }
        }
    }
