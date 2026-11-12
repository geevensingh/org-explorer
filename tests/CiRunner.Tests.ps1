$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tokens = $null
$errors = $null
$runner = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $root 'eng\Run-CiLane.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'CI runner syntax is invalid.' }
$source = Get-Content (Join-Path $root 'eng\Run-CiLane.ps1') -Raw
foreach ($required in @("'high-resolution'", "'--prepare-high-resolution'", "'--verify-standard-display'",
    "'--high-resolution'", 'HighResolutionScenarios.json')) {
    if (!$source.Contains($required)) { throw "Required separate high-resolution execution is missing $required" }
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
$results = Join-Path $root ('.ci-results\ci runner-' + [guid]::NewGuid().ToString('N'))
$artifactBuild = Join-Path $results 'build output'
$started = [DateTimeOffset]::UtcNow
$ceilingSeconds = 120
$steps = [Collections.Generic.List[object]]::new()
$index = 0
$null = New-Item -ItemType Directory -Path $results
try {
    Invoke-BudgetedDotnet restore @('restore', (Join-Path $root 'tests\OrgExplorer.Core.Tests'), '--locked-mode')
    if (!(Test-Path -LiteralPath (Join-Path $artifactBuild 'obj\OrgExplorer.Core.Tests\project.assets.json')) -or
        $steps.Count -ne 1 -or $steps[0].name -ne 'restore') {
        throw 'CI restore did not retain the complete artifacts path containing spaces.'
    }
    Write-Host 'PASS: the actual budgeted CI runner preserves arguments containing spaces.'
} finally {
    Remove-Item -LiteralPath $results -Recurse -Force
}
