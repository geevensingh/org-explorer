$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tokens = $null
$errors = $null
$runner = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $root 'eng\Run-CiLane.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'CI runner syntax is invalid.' }
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
} finally {
    Remove-Item -LiteralPath $results -Recurse -Force
}
