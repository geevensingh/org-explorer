param([string] $Repository, [string] $ExpectedRevision, [switch] $KernelEvents, [switch] $FileMetadataOnly)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $Repository
$results = Join-Path $Repository '.ci-results\cleanup-observed'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$references = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' -File).FullName) +
    (Join-Path $PSHOME 'System.Management.dll')
Add-Type -Path (Join-Path $PSScriptRoot 'OwnedCleanupTrace.cs') -ReferencedAssemblies $references
$trace = $null
$kernel = $null
$primaryFailure = $null
$diagnosticFailures = [Collections.Generic.List[string]]::new()
$kernelFile = Join-Path $results 'kernel.transient.etl'
$kernelXml = Join-Path $results 'kernel.transient.xml'
$kernelCount = 0
$fileObservations = [Collections.Generic.List[object]]::new()
function Read-OwnedFileMetadata([string] $Path, [string] $Boundary) {
    if (!('KernelCleanupTrace' -as [type]) -or !$Path.StartsWith($results + '\', [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($Path) -ine 'e_sqlite3.dll') { return }
    $record = @{ path = $Path; observedAt = [DateTimeOffset]::UtcNow.ToString('O'); boundary = $Boundary }
    try {
        $record.identity = [KernelCleanupTrace]::Identity($Path)
        $acl = Get-Acl -LiteralPath $Path
        $record.permission = @{ protected = $acl.AreAccessRulesProtected
            rules = @($acl.Access | ForEach-Object { @{ rights = $_.FileSystemRights.ToString()
                type = $_.AccessControlType.ToString(); inherited = $_.IsInherited } }) }
    } catch { $record.queryFailure = @{ errorType = $_.Exception.GetType().FullName; hresult = $_.Exception.HResult } }
    $fileObservations.Add($record)
}
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
    if ('KernelCleanupTrace' -as [type]) {
        foreach ($directory in @($Path) + @($LiteralPath)) {
            if ($directory) {
                Get-ChildItem -LiteralPath $directory -Filter e_sqlite3.dll -File -Recurse |
                    Where-Object { $_.FullName -like '*\win-x64\native\*' } |
                    ForEach-Object { Read-OwnedFileMetadata $_.FullName 'before-real-cleanup' }
            }
        }
    }
    try { Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters }
    catch {
        $cleanupFailure = $_
        $trace.Boundary("real-cleanup-refused;hresult=$($cleanupFailure.Exception.HResult);path=$($cleanupFailure.TargetObject)")
        $failedPath = if ($cleanupFailure.TargetObject -is [IO.FileSystemInfo]) {
            $cleanupFailure.TargetObject.FullName
        } else { [string]$cleanupFailure.TargetObject }
        Read-OwnedFileMetadata $failedPath 'real-cleanup-refused'
        throw
    }
    $trace.Boundary("after-real-cleanup;path=$Path$LiteralPath")
}
try {
    if ($KernelEvents -or $FileMetadataOnly) {
        Add-Type -Path (Join-Path $PSScriptRoot 'KernelCleanupTrace.cs')
        if (!$FileMetadataOnly) { $kernel = [KernelCleanupTrace]::new($kernelFile) }
    }
    $trace = [OwnedCleanupTrace]::new($PID, $results, (Join-Path $results 'owned-process-images.tsv'), !($KernelEvents -or $FileMetadataOnly))
    & (Join-Path $Repository 'eng\Run-Performance.ps1') -ExpectedRevision $ExpectedRevision -ResultsDirectory $results
} catch { $primaryFailure = $_
} finally {
    if ($trace) {
        try { $trace.Boundary('runner-returned'); $trace.Dispose() }
        catch { $diagnosticFailures.Add($_.Exception.Message) }
    }
    if ($kernel) {
        try {
            $kernel.Dispose()
            & tracerpt $kernelFile -o $kernelXml -of XML -y -gmt > (Join-Path $results 'kernel-decoder.log')
            if ($LASTEXITCODE -ne 0) { throw "Kernel trace decoder exited $LASTEXITCODE." }
            [xml]$events = Get-Content -LiteralPath $kernelXml -Raw
            $retained = [Collections.Generic.List[string]]::new()
            $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $requests = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($event in $events.SelectNodes("//*[local-name()='Event']")) {
                $data = @{}
                foreach ($value in $event.SelectNodes(".//*[local-name()='Data']")) {
                    $data[$value.GetAttribute('Name')] = $value.InnerText
                }
                $keys = @('FileObject', 'FileKey') | Where-Object { $data.ContainsKey($_) } |
                    ForEach-Object { $data[$_] } | Where-Object { $_ -and $_ -notmatch '^(0x)?0+$' }
                $named = @('FileName', 'OpenPath', 'OpenPathName', 'ImageFileName') |
                    Where-Object { $data.ContainsKey($_) } | ForEach-Object { $data[$_] }
                $exact = @($named | Where-Object { $_ -match '(?i)e_sqlite3\.dll$' -and
                    $_ -match '(?i)\\\.ci-results\\cleanup-observed\\' }).Count -gt 0
                if ($named.Count -and !$exact) {
                    foreach ($key in $keys) { $null = $files.Remove($key) }
                    continue
                }
                if ($exact) { foreach ($key in $keys) { $null = $files.Add($key) } }
                $known = $exact -or @($keys | Where-Object { $files.Contains($_) }).Count -gt 0
                if ($data.ContainsKey('IrpPtr') -and $requests.Contains($data.IrpPtr)) {
                    $known = $true
                    $null = $requests.Remove($data.IrpPtr)
                } elseif ($known -and $data.ContainsKey('IrpPtr')) { $null = $requests.Add($data.IrpPtr) }
                if ($known) { $retained.Add($event.OuterXml) }
                $opcode = $event.SelectSingleNode(".//*[local-name()='System']/*[local-name()='Opcode']")
                if ($opcode -and $opcode.InnerText -in @('35', '66')) {
                    foreach ($key in $keys) { $null = $files.Remove($key) }
                }
            }
            $kernelCount = $retained.Count
            $filteredXml = "<OwnedNativeImages>`n" + ($retained.ToArray() -join "`n") + "`n</OwnedNativeImages>"
            [IO.File]::WriteAllText((Join-Path $results 'kernel-owned-native-images.xml'), $filteredXml)
            if (!$kernelCount) { throw 'No exact owned-native kernel events decoded; unload evidence remains unavailable.' }
            if ($kernel.EventsLost -or $kernel.BuffersLost) { throw 'Kernel trace lost events/buffers; cannot assert complete unload evidence.' }
        } catch { $diagnosticFailures.Add($_.Exception.Message) }
        finally {
            foreach ($file in @($kernelFile, $kernelXml)) {
                if (Test-Path -LiteralPath $file) {
                    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $file -Force
                }
            }
        }
    }
    $elapsed = if ($env:SCALE_STARTED_AT) {
        ([DateTimeOffset]::UtcNow - [DateTimeOffset]::Parse($env:SCALE_STARTED_AT)).TotalSeconds
    } else { $null }
    if ($elapsed -gt 660) { $diagnosticFailures.Add('Diagnostic setup, workload, original cleanup and trace finalization exceeded the original 660s budget.') }
    @{ diagnosticOnly = $true; acceptingCohort = $false; expectedRevision = $ExpectedRevision
        kernelEvents = [bool]($KernelEvents -and !$FileMetadataOnly); fileMetadataOnly = [bool]$FileMetadataOnly
        modulePolling = !($KernelEvents -or $FileMetadataOnly); executionIncludingDiagnosticSeconds = $elapsed
        safetySeconds = 660; primaryFailure = if ($primaryFailure) { $primaryFailure.Exception.Message } else { $null }
        diagnosticFailures = @($diagnosticFailures.ToArray()); ownedNativeKernelEventCount = $kernelCount
        eventsLost = if ($kernel) { $kernel.EventsLost } else { $null }
        buffersLost = if ($kernel) { $kernel.BuffersLost } else { $null }
        fileMetadata = @($fileObservations.ToArray())
        scope = 'Only exact run-owned native-image/file kernel records and correlated opened-file/request lifetimes retained; no environment/command-line dump. No process control or deletion retry.'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'diagnostic.json')
}
if ($primaryFailure) { throw $primaryFailure }
if ($diagnosticFailures.Count) { throw ($diagnosticFailures -join "`n") }
