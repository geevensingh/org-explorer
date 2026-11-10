$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$release = Get-Content "$root\.github\workflows\release.yml" -Raw
foreach ($required in @(
    'workflows: [Continuous integration]', 'types: [completed]', 'branches: [main]',
    'group: org-explorer-production-release', 'cancel-in-progress: false',
    'github.event.workflow_run.event == ''push''', 'github.event.workflow_run.head_repository.full_name == github.repository',
    'github.event.workflow_run.conclusion == ''success''', 'github.event.workflow_run.head_branch == ''main''',
    'ref: ${{ github.event.workflow_run.head_sha }}', 'fetch-depth: 0', 'persist-credentials: false',
    'uses: ./.github/workflows/scale-verification.yml', 'revision: ${{ github.event.workflow_run.head_sha }}',
    'needs: [plan, scale]', 'needs.scale.result == ''success''', 'needs.plan.result == ''success''',
    'secrets.ORG_EXPLORER_UPDATE_PRIVATE_KEY_PKCS8', 'vars.ORG_EXPLORER_UPDATE_KEY_ID',
    'Invoke-Release.ps1 -Mode Plan', 'Invoke-Release.ps1 -Mode Publish', 'actions: read', 'contents: write'
)) {
    if (!$release.Contains($required)) { throw "Production release contract missing: $required" }
}
if ($release -match '(?m)^  (pull_request|push|workflow_dispatch):' -or
    $release -match '(?i)--clobber|--force|secrets:\s*inherit') { throw 'Production publication must have no PR/manual entry or force/implicit secret path.' }
$ci = Get-Content "$root\.github\workflows\ci.yml" -Raw
if ($ci -notmatch '(?s)run_release_verification:\s+description:[^\r\n]+\s+required: false\s+default: false\s+type: boolean' -or
    !$ci.Contains('github.event_name == ''workflow_dispatch'' && inputs.run_release_verification') -or
    !$ci.Contains('uses: ./.github/workflows/release-verification.yml')) { throw 'PR-safe release verification must be a default-off manual bridge.' }
$verification = Get-Content "$root\.github\workflows\release-verification.yml" -Raw
foreach ($required in @('workflow_call:', 'uses: ./.github/workflows/scale-verification.yml', 'revision: ${{ inputs.revision }}',
    'needs.scale.result == ''success''', 'Test-ReleaseCandidate.ps1', 'actions/download-artifact@v4',
    'non-ui-performance-${{ inputs.revision }}-${{ github.run_id }}-${{ github.run_attempt }}',
    'ui-scale-${{ inputs.revision }}-${{ github.run_id }}-${{ github.run_attempt }}')) {
    if (!$verification.Contains($required)) { throw "Fresh hosted verification contract missing: $required" }
}
if ($verification -match 'contents: write|secrets\.|Invoke-Release\.ps1 -Mode Publish') { throw 'Candidate verification must not acquire production secrets/publication permissions.' }
Write-Host 'PASS: trusted production wiring and secret-free default-off exact-candidate verification contracts.'
