$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $root ('.ci-results\passive-binding-contract-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results | Out-Null
$names = @('GITHUB_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT', 'GITHUB_ENV', 'REVIEWED_DIAGNOSTIC_REVISION')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
$source = 'd' * 40
$shipping = 'cae977166df7327b4a2d268a10a20578adf338e6'
$identities = @{
    HEAD = $source; 'HEAD^' = $shipping
    'HEAD^{tree}' = '1' * 40; 'HEAD:src' = '2' * 40; 'HEAD:tests' = '3' * 40; 'HEAD:eng' = '4' * 40
    'HEAD:.github/workflows/ci.yml' = '5' * 40; 'HEAD:.github/workflows/scale-verification.yml' = '6' * 40
    "$shipping`^{tree}" = 'ad625fa53e6592840e1e44e0bc5ef6e4ed65e9de'
    "$shipping`:src" = 'd0c87216bb9fcefe5391eb6a79dfeee23a75ae31'
    "$shipping`:tests" = 'f5b7e1b303529af29c2a1e8a34a89cd5b00cc24d'
    "$shipping`:eng" = 'f0d4df23d4361e01fc5be4d32bfeeecba2211b7d'
    "$shipping`:.github/workflows/scale-verification.yml" = '019d5924de3378d077fac76abc8c04452846d18a'
}
try {
    $env:GITHUB_SHA = $source
    $env:REVIEWED_DIAGNOSTIC_REVISION = $source
    $env:GITHUB_RUN_ID = 'support-run'
    $env:GITHUB_RUN_ATTEMPT = '1'
    $env:GITHUB_ENV = Join-Path $results 'owned-environment'
    # Replace only Git metadata in this support process. No hosted, UI or product command is invoked.
    function git {
        $identity = $args[-1]
        if (!$identities.ContainsKey($identity)) { $global:LASTEXITCODE = 1; return }
        $global:LASTEXITCODE = 0
        return $identities[$identity]
    }
    foreach ($worker in @('workspace', 'prefix')) {
        $directory = Join-Path $results $worker
        & "$root\eng\Write-PassiveSaveBinding.ps1" -Worker $worker -ResultsDirectory $directory
        $binding = Get-Content -LiteralPath (Join-Path $directory 'diagnostic-source-binding.json') -Raw | ConvertFrom-Json
        $budget = if ($worker -ceq 'workspace') { 900 } else { 660 }
        if (!$binding.diagnosticOnly -or $binding.sourceRevision -cne $source -or
            $binding.reviewedDiagnosticRevision -cne $source -or $binding.shippingRevision -cne $shipping -or
            $binding.setupInclusiveBudgetSeconds -ne $budget -or $binding.runAttempt -cne '1') {
            throw 'An exact diagnostic source/budget/attempt binding was lost.'
        }
        $roles = if ($worker -ceq 'workspace') {
            @('workspace-controlled-entry-exe', 'workspace-controlled-adoption-host')
        } else { @('rich-normal-entry-exe', 'rich-controlled-wpf-host') }
        if ($binding.normalEntryRole -cne $roles[0] -or $binding.controlledHostRole -cne $roles[1]) {
            throw 'Controlled workspace or controlled WPF output was mislabeled as the rich normal EXE.'
        }
    }
    function Require-Refusal([string] $Name) {
        $directory = Join-Path $results $Name
        $failed = $false
        try { & "$root\eng\Write-PassiveSaveBinding.ps1" -Worker prefix -ResultsDirectory $directory }
        catch { $failed = $true }
        if (!$failed -or (Test-Path -LiteralPath (Join-Path $directory 'diagnostic-source-binding.json'))) {
            throw "Diagnostic binding did not refuse $Name before creating success-shaped evidence."
        }
    }
    $env:REVIEWED_DIAGNOSTIC_REVISION = 'e' * 40
    Require-Refusal wrong-reviewed-pin
    $env:REVIEWED_DIAGNOSTIC_REVISION = $source
    $env:GITHUB_RUN_ATTEMPT = ''
    Require-Refusal missing-attempt
    $env:GITHUB_RUN_ATTEMPT = '1'
    $identities['HEAD^'] = 'e' * 40
    Require-Refusal wrong-shipping-parent
    $identities['HEAD^'] = $shipping
    $identities["$shipping`:src"] = 'e' * 40
    Require-Refusal altered-shipping-source
    Remove-Item Function:\git
    $ci = Get-Content -LiteralPath "$root\.github\workflows\ci.yml" -Raw
    $scale = Get-Content -LiteralPath "$root\.github\workflows\scale-verification.yml" -Raw
    function Job([string] $Workflow, [string] $Name) {
        return [regex]::Match($Workflow, "(?ms)^  $Name`:\r?`n(?<body>.*?)(?=^  [\w-]+:|\z)").Groups['body'].Value
    }
    foreach ($name in @('core', 'filesystem', 'high-resolution', 'ci', 'release-verification')) {
        if (!(Job $ci $name).Contains('!inputs.run_passive_save_diagnostics')) {
            throw "Unapproved $name job can run during the isolated capture."
        }
    }
    foreach ($name in @('non-ui-performance', 'ui-scale-tail', 'ui-scale')) {
        if (!(Job $scale $name).Contains('!inputs.passive_capture_only')) {
            throw "Unapproved $name scale job can run during the isolated capture."
        }
    }
    $workspace = Job $ci desktop
    if (!$workspace.Contains('''[{"partition":"people"}]''') -or !$workspace.Contains('partition: [people, workspace]') -or
        !$workspace.Contains('timeout-minutes: 15') -or !$workspace.Contains('inputs.run_passive_save_diagnostics && 2 || 1') -or
        !$workspace.Contains('LANE_STARTED_AT=') -or !$workspace.Contains('Run-CiLane.ps1 desktop -DesktopPartition ${{ matrix.partition }}') -or
        !$workspace.Contains('REVIEWED_DIAGNOSTIC_REVISION: ${{ inputs.diagnostic_revision }}')) {
        throw 'The exact original workspace invocation/setup budget or reviewed-pin gate changed.'
    }
    $prefix = Job $scale ui-scale-prefix
    if (!$prefix.Contains('timeout-minutes: 13') -or !$prefix.Contains('SCALE_STARTED_AT=') -or
        !$prefix.Contains('inputs.passive_capture_only && 2 || 1') -or
        !$prefix.Contains('Run-UiScale.ps1 -ExpectedRevision $env:TESTED_REVISION -Worker prefix -ResultsDirectory .ci-results\ui-scale-prefix -ExtendedObservation:([bool]::Parse($env:EXTENDED_OBSERVATION))') -or
        !$prefix.Contains('REVIEWED_DIAGNOSTIC_REVISION: ${{ inputs.reviewed_diagnostic_revision }}')) {
        throw 'The exact original prefix command/setup budget or reviewed-pin gate changed.'
    }
    $runner = Get-Content -LiteralPath "$root\eng\Run-CiLane.ps1" -Raw
    foreach ($original in @('"-p:UpdateTrustFile=$(Join-Path $updateFixture ''trust.json'')"',
        '"-p:UpdateEffectsFile=$(Join-Path $root ''tests\ControlledUpdateEffects.cs'')"',
        "'--verify-update-yield'")) {
        if (!$runner.Contains($original)) { throw 'An existing workspace effect/assertion was changed.' }
    }
    if (!(Job $ci scale-verification).Contains('uses: ./.github/workflows/scale-verification.yml') -or
        !(Job $ci scale-verification).Contains('reviewed_diagnostic_revision: ${{ inputs.diagnostic_revision }}')) {
        throw 'Capture must use the registered ci.yml bridge at its exact reviewed diagnostic source.'
    }
    Write-Host 'PASS: exact reviewed source/budgets/roles and four fail-closed binding cases; original two workers, no extra scale/union/release jobs.'
} finally {
    if (Test-Path Function:\git) { Remove-Item Function:\git }
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    Remove-Item -LiteralPath $results -Recurse -Force
}
