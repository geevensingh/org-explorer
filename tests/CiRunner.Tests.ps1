$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tokens = $null
$errors = $null
$runner = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $root 'eng\Run-CiLane.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'CI runner syntax is invalid.' }
if (!@($runner.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'DesktopPartition' }).Count) {
    throw 'CI must explicitly select an isolated ordinary desktop partition.'
}
if (!@($runner.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'HighResolutionPartition' }).Count) {
    throw 'CI must explicitly select an isolated high-resolution partition.'
}
$contractsAssignment = $runner.Find({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -eq '$runDesktopContracts'
}, $true)
if (!$contractsAssignment) { throw 'Compiled/update/release execution must be assigned explicitly.' }
foreach ($case in @(
    @{ lane = 'desktop'; partition = ''; expected = $true },
    @{ lane = 'desktop'; partition = 'people'; expected = $false },
    @{ lane = 'desktop'; partition = 'workspace'; expected = $true },
    @{ lane = 'high-resolution'; partition = ''; expected = $false }
)) {
    $Lane = $case.lane
    $DesktopPartition = $case.partition
    . ([scriptblock]::Create($contractsAssignment.Extent.Text))
    if ($runDesktopContracts -ne $case.expected) { throw 'A required desktop companion command was dropped or duplicated.' }
}
$source = (Get-Content (Join-Path $root 'eng\Run-CiLane.ps1') -Raw) +
    (Get-Content (Join-Path $root 'eng\CoreContracts.ps1') -Raw)
foreach ($required in @("'high-resolution'", "'--prepare-high-resolution'", "'--verify-standard-display'",
    "'--high-resolution'", 'HighResolutionScenarios.json', "'--verify-update-yield'", 'ImplementationBaseline.Tests.ps1')) {
    if (!$source.Contains($required)) { throw "Required CI execution is missing $required" }
}
$uiScale = Get-Content (Join-Path $root 'eng\Run-UiScale.ps1') -Raw
foreach ($required in @("'--prepare-high-resolution'", "'--verify-high-resolution'")) {
    if (!$uiScale.Contains($required)) { throw "Explicit UI scale must share verified display preparation: $required" }
}
$command = $runner.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Invoke-BudgetedDotnet'
}, $true)
if (!$command) { throw 'Missing budgeted CI command.' }
. ([scriptblock]::Create($command.Extent.Text))
$companion = $runner.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Invoke-CiCompanion'
}, $true)
if (!$companion) { throw 'Missing CI companion execution.' }
. ([scriptblock]::Create($companion.Extent.Text))
$budgetAssignment = $runner.Find({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -eq '$ceilingSeconds'
}, $true)
if (!$budgetAssignment) { throw 'Missing CI lane budget selection.' }
$results = Join-Path $root ('.ci-results\ci runner-' + [guid]::NewGuid().ToString('N'))
$artifactBuild = Join-Path $results 'build output'
$steps = [Collections.Generic.List[object]]::new()
$index = 0
$null = New-Item -ItemType Directory -Path $results
try {
    $companionSteps = [Collections.Generic.List[object]]::new()
    'published component' | Set-Content -LiteralPath (Join-Path $results 'component.txt')
    $component = Invoke-CiCompanion 'read-published-component' {
        Get-Content -LiteralPath (Join-Path $results 'component.txt')
    } $companionSteps
    if ($component -cne 'published component' -or $companionSteps.Count -ne 1 -or
        $companionSteps[0].status -cne 'Passed') {
        throw 'A desktop companion lost its caller-owned published component path or successful receipt.'
    }
    $companionFailure = $null
    try {
        Invoke-CiCompanion 'reject-published-component' {
            $component = Get-Content -LiteralPath (Join-Path $results 'component.txt')
            throw "Rejected $component"
        } $companionSteps
    } catch { $companionFailure = $_ }
    if (!$companionFailure -or $companionFailure.Exception.Message -cne 'Rejected published component' -or
        $companionSteps.Count -ne 2 -or $companionSteps[1].status -cne 'Failed' -or
        $companionSteps[0].status -cne 'Passed') {
        throw 'A desktop companion lost its caller-owned path or initiating failure when retaining timing.'
    }
    Write-Host 'PASS: actual CI companions retain the caller-owned published path and independent success/failure receipts.'
    foreach ($Lane in @('desktop', 'high-resolution')) {
        . ([scriptblock]::Create($budgetAssignment.Extent.Text))
        $started = [DateTimeOffset]::UtcNow.AddSeconds(-661)
        $previousSteps = $steps.Count
        Invoke-BudgetedDotnet restore @('restore', (Join-Path $root 'tests\OrgExplorer.Core.Tests'), '--locked-mode')
        if (!(Test-Path -LiteralPath (Join-Path $artifactBuild 'obj\OrgExplorer.Core.Tests\project.assets.json')) -or
            $steps.Count -ne $previousSteps + 1 -or $steps[$previousSteps].name -ne 'restore') {
            throw 'CI restore did not retain the complete artifacts path containing spaces.'
        }
        Write-Host "PASS: $Lane work can continue after eleven minutes within its fifteen-minute budget."
    }
    Write-Host 'PASS: the actual budgeted CI runner preserves arguments containing spaces.'
    foreach ($Lane in @('core', 'filesystem', 'desktop', 'high-resolution')) {
        . ([scriptblock]::Create($budgetAssignment.Extent.Text))
        $expiredSeconds = if ($Lane -eq 'core') {
            . "$root\eng\TimingPolicy.ps1"
            (Get-ApprovedCoreTimingPolicy (Get-SourceTimingPolicyVersion (git -C $root rev-parse HEAD).Trim() -Core)).safetySeconds + 1
        } elseif ($Lane -in @('desktop', 'high-resolution')) { 901 } else { 661 }
        $started = [DateTimeOffset]::UtcNow.AddSeconds(-$expiredSeconds)
        $rejected = $false
        try {
            Invoke-BudgetedDotnet restore @('restore', (Join-Path $root 'tests\OrgExplorer.Core.Tests'), '--locked-mode')
        } catch {
            if ($_.Exception.Message -ne 'The lane budget (including setup) is exhausted.') { throw }
            $rejected = $true
        }
        if (!$rejected -or $steps.Count -ne 2) { throw "Expired $Lane work was not rejected before launch." }
    }
    Write-Host 'PASS: both desktop lanes reject work after fifteen minutes; core expiry follows its trusted source and filesystem remains unchanged.'
    $started = [DateTimeOffset]::UtcNow
    $failed = $false
    try { Invoke-BudgetedDotnet failed-command @('test', '--not-a-supported-option') }
    catch {
        if (!$_.Exception.Message.StartsWith('failed-command failed with exit code ')) { throw }
        $failed = $true
    }
    if (!$failed -or $steps[-1].status -cne 'Failed' -or $steps[0].status -cne 'Passed') {
        throw 'Execution metadata hid an actual command failure.'
    }
    $rejected = $false
    try { & "$root\eng\Run-CiLane.ps1" core -DesktopPartition people }
    catch {
        if ($_.Exception.Message -ne 'Only ordinary desktop execution supports partitions.') { throw }
        $rejected = $true
    }
    if (!$rejected) { throw 'A desktop partition changed another lane.' }
    foreach ($case in @(
        @{ lane = 'core'; desktop = ''; high = 'reading' },
        @{ lane = 'desktop'; desktop = ''; high = 'context' },
        @{ lane = 'high-resolution'; desktop = 'people'; high = 'reading' }
    )) {
        $rejected = $false
        $arguments = @{ Lane = $case.lane; HighResolutionPartition = $case.high }
        if ($case.desktop) { $arguments.DesktopPartition = $case.desktop }
        try { & "$root\eng\Run-CiLane.ps1" @arguments }
        catch {
            if ($_.Exception.Message -notin @('Only ordinary desktop execution supports partitions.',
                'Only high-resolution desktop execution supports high-resolution partitions.')) { throw }
            $rejected = $true
        }
        if (!$rejected) { throw 'Resolution partition selection changed another lane.' }
    }
    $fixture = Join-Path $results 'partition-fixture'
    New-Item -ItemType Directory -Path (Join-Path $fixture 'eng'), (Join-Path $fixture 'tests') -Force | Out-Null
    foreach ($name in @('Run-CiLane.ps1', 'DesktopResults.ps1', 'CoreContracts.ps1', 'TimingPolicy.ps1')) {
        Copy-Item -LiteralPath (Join-Path $root "eng\$name") -Destination (Join-Path $fixture "eng\$name")
    }
    foreach ($name in @('DesktopScenarios.json', 'DesktopPartitions.json',
        'HighResolutionScenarios.json', 'HighResolutionPartitions.json')) {
        Copy-Item -LiteralPath (Join-Path $root "tests\$name") -Destination (Join-Path $fixture "tests\$name")
    }
    $previousDirectory = Get-Location
    $previousStarted = $env:LANE_STARTED_AT
    $previousDesktopResults = $env:DESKTOP_RESULTS_DIRECTORY
    $previousTiming = $env:SCALE_TIMING_FILE
    try {
        foreach ($partition in @('reading', 'context')) {
            $env:LANE_STARTED_AT = [DateTimeOffset]::UtcNow.AddSeconds(-901).ToString('O')
            $rejected = $false
            try { & (Join-Path $fixture 'eng\Run-CiLane.ps1') high-resolution -HighResolutionPartition $partition }
            catch {
                if ($_.Exception.Message -ne 'The lane budget (including setup) is exhausted.') { throw }
                $rejected = $true
            }
            $metadata = Get-Content -LiteralPath (Join-Path $fixture ".ci-results\high-resolution-$partition\execution.json") -Raw |
                ConvertFrom-Json
            if (!$rejected -or $metadata.lane -cne 'high-resolution' -or $metadata.partition -cne $partition -or
                $metadata.complete -or $metadata.ceilingSeconds -ne 900 -or $metadata.executionSeconds -le 900 -or
                @($metadata.steps).Count -ne 0) {
                throw 'A high-resolution partition did not retain its isolated results and expired-deadline failure.'
            }
        }
    } finally {
        Set-Location -LiteralPath $previousDirectory
        $env:LANE_STARTED_AT = $previousStarted
        $env:DESKTOP_RESULTS_DIRECTORY = $previousDesktopResults
        $env:SCALE_TIMING_FILE = $previousTiming
    }
    Write-Host 'PASS: failed command metadata stays failed and desktop partitioning cannot alter another lane.'
    & "$PSScriptRoot\CoreContracts.Tests.ps1"
} finally {
    Remove-Item -LiteralPath $results -Recurse -Force
}
