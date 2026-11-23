param([string] $Assembly = (Join-Path $PSScriptRoot 'OrgExplorer.Core.Tests\bin\Release\net10.0\OrgExplorer.Core.Tests.dll'))
$ErrorActionPreference = 'Stop'
$assemblyPath = (Resolve-Path -LiteralPath $Assembly).Path
$results = Join-Path "$PSScriptRoot\..\.ci-results" ('draft-discard-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $results
try {
    dotnet test $assemblyPath `
        --filter 'FullyQualifiedName~PersonNoteTests.In_flight_discard_preserves_exact_writing_and_idle_discard_clears_only_its_scope' `
        --logger 'trx;LogFileName=discard.trx' --results-directory $results --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'The public workflow/view-model discard contract failed.' }
    [xml] $run = Get-Content -LiteralPath (Join-Path $results 'discard.trx') -Raw
    $counters = $run.TestRun.ResultSummary.Counters
    $cases = @($run.TestRun.Results.UnitTestResult)
    if ($run.TestRun.ResultSummary.outcome -ne 'Completed' -or
        $counters.total -ne '2' -or $counters.executed -ne '2' -or $counters.passed -ne '2' -or
        $counters.failed -ne '0' -or $counters.notExecuted -ne '0' -or
        $cases.Count -ne 2 -or @($cases | Where-Object outcome -ne Passed).Count -ne 0) {
        throw 'Discard verification requires both complete Person/Team public-seam cases; missing, skipped or partial results cannot pass.'
    }
    Write-Host 'PASS: actual shared Person/Team draft coordinator retains admitted owner writing and scoped discard preserves independent drafts. Real SQLite counterparts remain in the filesystem lane.'
} finally {
    Remove-Item -LiteralPath $results -Recurse -Force
}
