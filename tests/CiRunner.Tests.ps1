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
$source = Get-Content (Join-Path $root 'eng\Run-CiLane.ps1') -Raw
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
        $expiredSeconds = if ($Lane -eq 'core') { 301 } elseif ($Lane -in @('desktop', 'high-resolution')) { 901 } else { 661 }
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
    Write-Host 'PASS: both desktop lanes reject work after fifteen minutes; core and filesystem deadlines remain unchanged.'
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
    Write-Host 'PASS: failed command metadata stays failed and desktop partitioning cannot alter another lane.'
} finally {
    Remove-Item -LiteralPath $results -Recurse -Force
}
