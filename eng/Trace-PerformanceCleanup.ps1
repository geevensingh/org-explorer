param([string] $Repository, [string] $ExpectedRevision)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $Repository
$results = Join-Path $Repository '.ci-results\cleanup-observed'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$references = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' -File).FullName) +
    (Join-Path $PSHOME 'System.Management.dll')
Add-Type -Path (Join-Path $PSScriptRoot 'OwnedCleanupTrace.cs') -ReferencedAssemblies $references
$trace = [OwnedCleanupTrace]::new($PID, $results, (Join-Path $results 'owned-process-images.tsv'))
function Start-Process {
    [CmdletBinding()]
    param([string] $FilePath, [string[]] $ArgumentList, [switch] $NoNewWindow, [switch] $PassThru,
        [string] $RedirectStandardOutput, [string] $RedirectStandardError)
    $process = Microsoft.PowerShell.Management\Start-Process @PSBoundParameters
    $trace.Launched($process.Id, $PID)
    return $process
}
function Remove-Item {
    [CmdletBinding()]
    param([string[]] $Path, [string[]] $LiteralPath, [switch] $Recurse, [switch] $Force)
    $trace.Boundary("before-real-cleanup;path=$Path$LiteralPath")
    try { Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters }
    catch {
        $trace.Boundary("real-cleanup-refused;hresult=$($_.Exception.HResult);path=$($_.TargetObject)")
        throw
    }
    $trace.Boundary("after-real-cleanup;path=$Path$LiteralPath")
}
try {
    & (Join-Path $Repository 'eng\Run-Performance.ps1') -ExpectedRevision $ExpectedRevision -ResultsDirectory $results
} finally {
    $trace.Boundary('runner-returned')
    $trace.Dispose()
}
