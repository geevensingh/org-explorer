$PerformanceFilter = 'Category=Performance'
$PerformanceTests = @(
    'OrgExplorer.Persistence.Tests.ScaleWorkflowTests.Ten_thousand_People_keep_complete_durable_current_and_frozen_context_through_mutations_and_delivery'
)

function Get-PerformanceConfiguration {
    $root = Split-Path $PSScriptRoot -Parent
    $projectDirectory = Join-Path $root 'tests\OrgExplorer.Persistence.Tests'
    [xml]$project = Get-Content -LiteralPath (Join-Path $projectDirectory 'OrgExplorer.Persistence.Tests.csproj') -Raw
    $paths = @('tests/OrgExplorer.Persistence.Tests', 'tests/OrgExplorer.Storage.ProcessFixture', 'tests/Fixtures',
        'Directory.Build.props', 'Directory.Build.targets', 'global.json', 'eng/Run-Performance.ps1',
        'eng/ScaleRunner.ps1', 'eng/PerformanceSuite.ps1')
    foreach ($compile in @($project.SelectNodes('/Project/ItemGroup/Compile'))) {
        $path = [IO.Path]::GetFullPath((Join-Path $projectDirectory $compile.GetAttribute('Include')))
        $paths += [IO.Path]::GetRelativePath($root, $path).Replace('\', '/')
    }
    $tree = @(git ls-tree -r HEAD -- @paths)
    if ($LASTEXITCODE -ne 0 -or !$tree.Count) { throw 'Selected workload/linked-fixture identity could not be resolved.' }
    $fixtureTree = ($tree -join "`n") | git hash-object --stdin
    if ($LASTEXITCODE -ne 0) { throw 'Selected workload identity could not be hashed.' }
    $testTree = git rev-parse --verify HEAD:tests/OrgExplorer.Persistence.Tests
    if ($LASTEXITCODE -ne 0) { throw 'Persistence test-tree identity unavailable.' }
    $sdk = dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'Resolved SDK identity unavailable.' }
    $runtimes = @(dotnet --list-runtimes | ForEach-Object { $_ -replace ' \[.*\]$', '' })
    if ($LASTEXITCODE -ne 0 -or !$runtimes) { throw 'Resolved runtime identity unavailable.' }
    return @{
        workload = 'OrgExplorer.Persistence.Tests'; testTree = $testTree; fixtureTree = $fixtureTree
        sdk = $sdk; runtimes = $runtimes; os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        processors = [Environment]::ProcessorCount; image = $env:ImageOS; imageVersion = $env:ImageVersion
        powershell = $PSVersionTable.PSVersion.ToString()
        setup = 'locked-restore;isolated-build;setup-and-cleanup-included'
    }
}

function Assert-PerformanceResults([string] $Directory) {
    $files = @(Get-ChildItem $Directory -Filter '*.trx' -File)
    if ($files.Count -ne 1) { throw 'Expected exactly one fresh performance TRX result.' }
    [xml] $run = Get-Content $files[0].FullName -Raw
    $summary = $run.TestRun.ResultSummary
    $counters = $summary.Counters
    $results = @($run.TestRun.Results.UnitTestResult)
    if ($summary.outcome -ne 'Completed' -or
        $counters.total -ne $PerformanceTests.Count -or
        $counters.executed -ne $PerformanceTests.Count -or
        $counters.passed -ne $PerformanceTests.Count -or
        $counters.failed -ne '0' -or $counters.notExecuted -ne '0' -or
        $results.Count -ne $PerformanceTests.Count) {
        throw 'Performance execution was failed, skipped, missing or incomplete.'
    }
    foreach ($test in $PerformanceTests) {
        $match = @($results | Where-Object { $_.testName -eq $test })
        if ($match.Count -ne 1 -or $match[0].outcome -ne 'Passed') {
            throw "Required performance workload did not pass: $test"
        }
    }
}
