param(
    [Parameter(Mandatory)][string]$Harness
)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -eq 'WDAGUtilityAccount') { throw 'The fail-closed host guard test must run outside Windows Sandbox.' }
foreach ($scenario in 'no-isolation', 'shared-runner', 'foreign-repository', 'outside-checkout') {
    $start = [Diagnostics.ProcessStartInfo]::new((Resolve-Path $Harness).Path)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($scenario -ne 'no-isolation') {
        foreach ($argument in '--github-hosted', 'C:\not-the-checkout\packages', 'C:\not-the-checkout\smoke.exe',
            'C:\not-the-checkout\lifecycle', 'C:\not-the-checkout\results') {
            $start.ArgumentList.Add($argument)
        }
        $start.Environment['CI'] = 'true'
        $start.Environment['GITHUB_ACTIONS'] = 'true'
        $start.Environment['RUNNER_OS'] = 'Windows'
        $start.Environment['RUNNER_ENVIRONMENT'] = if ($scenario -eq 'shared-runner') { 'self-hosted' } else { 'github-hosted' }
        $start.Environment['GITHUB_REPOSITORY'] = if ($scenario -eq 'foreign-repository') { 'other/repository' } else { 'geevensingh/org-explorer' }
        $start.Environment['GITHUB_WORKSPACE'] = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(10000)) { throw 'The installer harness did not refuse host execution promptly.' }
        $message = $output.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 1 -or $message -notmatch 'No host installation was attempted' -or
            $message -notmatch 'disposable Windows Sandbox or ephemeral GitHub-hosted Windows') {
            throw "Installer isolation guard did not reject $scenario before executing setup. $message"
        }
        Write-Host "PASS: installer isolation guard refuses $scenario."
    } finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
