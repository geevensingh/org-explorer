param(
    [Parameter(Mandatory)][string]$ExpectedRevision,
    [Parameter(Mandatory)][string]$ResultsDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$revision = git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $ExpectedRevision -notmatch '^[0-9a-fA-F]{40}$' -or $revision -cne $ExpectedRevision) {
    throw 'Stage1 requires the exact checked-out candidate revision.'
}
$record = [ordered]@{
    schemaVersion = 1
    stage = 'SqliteNativeTraceStage1FeasibilityOnly'
    diagnosticOnly = $true
    normalCiAttested = $false
    revision = $revision
    runId = $env:GITHUB_RUN_ID
    runAttempt = $env:GITHUB_RUN_ATTEMPT
    status = 'RefusedHost'
    collectionStarted = $false
    fixtureStarted = $false
    nativeBinaryChanged = $false
    privilegeAdjusted = $false
    nativeDmsState = 'Unknown'
    ownedFixturePids = @()
    permissions = $null
    provider = $null
    filtering = [ordered]@{
        providerPidScope = 'NotEstablished'
        knownFilePreCollectionScope = 'NotEstablished'
        privateProcessInventory = $false
        postCollectionFilteringAllowed = $false
        collectionImplementationIncluded = $false
    }
    limitations = @('Hosted token and provider-specific pre-collection filtering have not been established.')
}
$exitCode = 2
try {
    $hosted = $IsWindows -and $env:GITHUB_ACTIONS -ceq 'true' -and
        $env:RUNNER_ENVIRONMENT -ceq 'github-hosted' -and $env:RUNNER_OS -ceq 'Windows' -and
        $env:GITHUB_EVENT_NAME -ceq 'workflow_dispatch' -and
        $env:GITHUB_REPOSITORY -ceq 'geevensingh/org-explorer' -and
        $env:SQLITE_STAGE1_MODE -ceq 'feasibility-only'
    if ($hosted) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        try {
            $principal = [Security.Principal.WindowsPrincipal]::new($identity)
            $privilegeRows = @(whoami /priv /fo csv /nh | ConvertFrom-Csv -Header Name,Description,State)
            if ($LASTEXITCODE -ne 0) { throw 'The hosted token privilege query failed.' }
            $profile = @($privilegeRows | Where-Object Name -ceq 'SeSystemProfilePrivilege')
            $record.permissions = [ordered]@{
                elevatedAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
                systemProfilePrivilegePresent = $profile.Count -eq 1
                systemProfileReportedState = if ($profile.Count -eq 1) { $profile[0].State } else { 'Absent' }
                tracePermissionValidated = $false
            }
        }
        finally { $identity.Dispose() }
        $provider = Get-WinEvent -ListProvider Microsoft-Windows-Kernel-File
        $schemas = [Collections.Generic.List[object]]::new()
        foreach ($id in @(64,65,66,67,68,76)) {
            $events = @($provider.Events | Where-Object Id -eq $id)
            foreach ($event in $events) {
                [xml]$template = $event.Template
                $fields = @($template.SelectNodes('//*[local-name()="data"]') | ForEach-Object { $_.GetAttribute('name') })
                if ($fields.Count -gt 32) { throw 'Provider schema exceeds the bounded metadata selection.' }
                $schemas.Add([ordered]@{
                    eventId = $event.Id
                    version = $event.Version
                    fields = $fields
                    fileNameFieldPresent = @($fields | Where-Object { $_ -in @('FileName','OpenPath') }).Count -gt 0
                    fileObjectFieldPresent = $fields -contains 'FileObject'
                })
            }
        }
        $record.provider = [ordered]@{
            name = 'Microsoft-Windows-Kernel-File'
            id = $provider.Id.ToString()
            selectedSchemaCount = $schemas.Count
            schemas = $schemas.ToArray()
            metadataOnly = $true
        }
        # Generic scope filters or event fields alone do not prove kernel-provider file/PID isolation.
        $record.status = 'BlockedPreCollectionFiltering'
        $record.limitations = @(
            'Provider-specific PID scope for this kernel provider is not demonstrated.',
            'Known DB/WAL/SHM pre-collection filtering across create/read/write/close is not demonstrated.',
            'File-object association learned after collection cannot satisfy the approved pre-collection scope.',
            'Owned future test/child PIDs are not created or approximated with an executable-name filter.',
            'No SQLite fixture or trace may start until the owner reconciles this feasibility result.'
        )
    }
}
catch {
    $record.status = 'FeasibilityError'
    $record['errorType'] = $_.Exception.GetType().Name
    $record['errorHResult'] = $_.Exception.HResult
}
finally {
    New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
    $json = $record | ConvertTo-Json -Depth 8
    if ($json.Length -gt 16384) { throw 'Stage1 feasibility metadata exceeded its bound.' }
    Set-Content -LiteralPath (Join-Path $ResultsDirectory 'feasibility.json') -Value $json -Encoding utf8
    Write-Host "SQLITE-STAGE1-FEASIBILITY status=$($record.status); diagnosticOnly=true; collectionStarted=false; fixtureStarted=false; normalCiAttested=false"
}
exit $exitCode
