$PerformanceFilter = 'Category=Performance'
$PerformanceTests = @(
    'OrgExplorer.Persistence.Tests.ScaleWorkflowTests.Ten_thousand_People_keep_complete_durable_current_and_frozen_context_through_mutations_and_delivery'
)

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
